using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using Emgu.CV;
using Emgu.CV.CvEnum;
using SkiaSharp;
using SmartWatchProj.Models.Devices;
using SmartWatchProj.Services.Diagnostics;
using YoloDotNet;
using YoloDotNet.Core;
using YoloDotNet.Models;

namespace SmartWatchProj.Services.Devices
{
    public sealed class DevicePreflightService
    {
        private const int CameraIndex = 0;
        private const string LinuxPrimaryVideoDevice = "/dev/video0";
        private const string ConfigFileName = "device-port-map.json";
        private readonly string baseDirectory;
        private readonly RuntimeLogStore logStore;

        public DevicePreflightService(string baseDirectory, RuntimeLogStore logStore)
        {
            this.baseDirectory = baseDirectory;
            this.logStore = logStore;
        }

        public IReadOnlyList<DeviceStatusSnapshot> Check(bool diagnosticsMode, CancellationToken cancellationToken = default)
        {
            var config = TryLoadConfig();
            var controller = config.Controller;
            var serialReport = ProbeSerialPorts();
            var controllerProbe = ProbeController(controller, cancellationToken);
            var noCameraMode = OperatingSystem.IsLinux() && config.LinuxNoCameraMode;
            var cameraProbe = noCameraMode
                ? new CameraProbeResult(BuildCameraDisabledStatus())
                : ProbeCamera(config.CameraRotation);
            var yoloStatus = noCameraMode
                ? BuildYoloDisabledStatus()
                : ProbeYoloModel(cameraProbe, diagnosticsMode);

            if (noCameraMode)
            {
                logStore.Warning("Mode", "Linux no-camera mode enabled");
                logStore.Warning("Presence", "Camera stage bypassed by config");
            }

            return new List<DeviceStatusSnapshot>
            {
                cameraProbe.Status,
                yoloStatus,
                ProbeComEnvironment(serialReport, controller),
                BuildControllerConnectionStatus(serialReport, controller, controllerProbe, diagnosticsMode),
                BuildControllerCommandStatus(controller, controllerProbe, diagnosticsMode),
                BuildControllerDataStatus(controllerProbe)
            };
        }

        private static DeviceStatusSnapshot BuildCameraDisabledStatus() =>
            new()
            {
                Id = "camera",
                DisplayName = "Камера",
                State = DeviceReadinessState.Disabled,
                StatusLabel = "Отключено для Linux test mode",
                Detail = "Камера отключена для текущего Linux test режима и не участвует в preflight/runtime.",
                IsBlocking = false,
                BlockingLabel = "Камера отключена конфигом"
            };

        private static DeviceStatusSnapshot BuildYoloDisabledStatus() =>
            new()
            {
                Id = "yolo",
                DisplayName = "YOLO / presence",
                State = DeviceReadinessState.Disabled,
                StatusLabel = "Отключено для Linux test mode",
                Detail = "Presence/YOLO отключены вместе с камерой для текущего Linux test режима.",
                IsBlocking = false,
                BlockingLabel = "Presence отключен конфигом"
            };

        private CameraProbeResult ProbeCamera(int cameraRotation)
        {
            var linuxDevices = GetLinuxVideoDevices();
            var selectedDevice = GetPrimaryLinuxVideoDevice(linuxDevices);
            var permissionNote = GetLinuxCameraPermissionNote();

            if (OperatingSystem.IsLinux() && selectedDevice is null)
            {
                return new CameraProbeResult(new DeviceStatusSnapshot
                {
                    Id = "camera",
                    DisplayName = "Камера",
                    State = DeviceReadinessState.Unavailable,
                    StatusLabel = "Камера не найдена",
                    Detail = $"Ожидался основной video device {LinuxPrimaryVideoDevice}. Обнаружено: {(linuxDevices.Count == 0 ? "нет video-устройств" : string.Join(", ", linuxDevices))}.",
                    IsBlocking = false,
                    BlockingLabel = "Блокирует реальный запуск: камера отсутствует"
                });
            }

            try
            {
                using var probe = OperatingSystem.IsLinux()
                    ? new VideoCapture(CameraIndex, VideoCapture.API.V4L2)
                    : new VideoCapture(CameraIndex);

                if (!probe.IsOpened)
                {
                    return new CameraProbeResult(new DeviceStatusSnapshot
                    {
                        Id = "camera",
                        DisplayName = "Камера",
                        State = permissionNote is null ? DeviceReadinessState.Error : DeviceReadinessState.Unavailable,
                        StatusLabel = permissionNote is null ? "Camera open failed" : "Нет прав к камере",
                        Detail = $"Не удалось открыть {(selectedDevice ?? LinuxPrimaryVideoDevice)} через {(OperatingSystem.IsLinux() ? "V4L2" : "default backend")}. {BuildCameraEnvironmentDetail(linuxDevices, selectedDevice, permissionNote)}",
                        IsBlocking = false,
                        BlockingLabel = "Блокирует реальный запуск: камера не открывается"
                    });
                }

                using var frame = new Mat();
                for (var attempt = 1; attempt <= 4; attempt++)
                {
                    if (!probe.Read(frame) || frame.IsEmpty)
                    {
                        Thread.Sleep(150);
                        continue;
                    }

                    using var skBitmap = TryConvertMatToSkBitmap(frame);
                    if (skBitmap is null)
                    {
                        return new CameraProbeResult(new DeviceStatusSnapshot
                        {
                            Id = "camera",
                            DisplayName = "Камера",
                            State = DeviceReadinessState.Error,
                            StatusLabel = "Ошибка обработки кадра",
                            Detail = $"Камера открылась, но кадр не удалось преобразовать без OpenCV encode path. {BuildCameraEnvironmentDetail(linuxDevices, selectedDevice, permissionNote)}",
                            IsBlocking = false,
                            BlockingLabel = "Блокирует реальный запуск: кадр камеры не обрабатывается"
                        });
                    }

                    using var rotatedBitmap = ApplyCameraRotation(skBitmap, cameraRotation);
                    using var image = SKImage.FromBitmap(rotatedBitmap);
                    using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
                    return new CameraProbeResult(new DeviceStatusSnapshot
                    {
                        Id = "camera",
                        DisplayName = "Камера",
                        State = DeviceReadinessState.Ready,
                        StatusLabel = "Кадр получен",
                        Detail = $"Устройство {(selectedDevice ?? $"index {CameraIndex}")}, backend {(OperatingSystem.IsLinux() ? "V4L2" : "default")}, первый кадр получен ({frame.Width}x{frame.Height}, попытка {attempt}). {BuildCameraEnvironmentDetail(linuxDevices, selectedDevice, permissionNote)}",
                        IsBlocking = false
                    }, data.ToArray());
                }

                return new CameraProbeResult(new DeviceStatusSnapshot
                {
                    Id = "camera",
                    DisplayName = "Камера",
                    State = DeviceReadinessState.Unavailable,
                    StatusLabel = "Кадр не получен",
                    Detail = $"Камера открылась, но за 4 попытки не отдала кадр. {BuildCameraEnvironmentDetail(linuxDevices, selectedDevice, permissionNote)}",
                    IsBlocking = false,
                    BlockingLabel = "Блокирует реальный запуск: камера не отдаёт кадр"
                });
            }
            catch (Exception ex)
            {
                var permissionRelated = IsPermissionError(ex);
                return new CameraProbeResult(new DeviceStatusSnapshot
                {
                    Id = "camera",
                    DisplayName = "Камера",
                    State = permissionRelated ? DeviceReadinessState.Unavailable : DeviceReadinessState.Error,
                    StatusLabel = permissionRelated ? "Нет прав к камере" : "Camera open failed",
                    Detail = $"Ошибка открытия {(selectedDevice ?? LinuxPrimaryVideoDevice)}: {ex.Message}. {BuildCameraEnvironmentDetail(linuxDevices, selectedDevice, permissionNote)}",
                    IsBlocking = false,
                    BlockingLabel = permissionRelated
                        ? "Блокирует реальный запуск: недостаточно прав к камере"
                        : "Блокирует реальный запуск: ошибка открытия камеры"
                });
            }
        }

        private DeviceStatusSnapshot ProbeYoloModel(CameraProbeResult cameraProbe, bool diagnosticsMode)
        {
            if (cameraProbe.SampleFrameBytes is null)
            {
                return new DeviceStatusSnapshot
                {
                    Id = "yolo",
                    DisplayName = "YOLO / presence",
                    State = DeviceReadinessState.Skipped,
                    StatusLabel = "Пропущено из-за камеры",
                    Detail = $"YOLO skipped because camera state is {cameraProbe.Status.StateText}.",
                    IsBlocking = false,
                    BlockingLabel = "Блокирует реальный запуск: YOLO не может стартовать без кадра"
                };
            }

            var modelPath = Path.Combine(baseDirectory, "Assets", "yolov8n.onnx");
            if (!File.Exists(modelPath))
            {
                return new DeviceStatusSnapshot
                {
                    Id = "yolo",
                    DisplayName = "YOLO / presence",
                    State = DeviceReadinessState.Missing,
                    StatusLabel = "Модель не найдена",
                    Detail = $"Файл модели отсутствует: {modelPath}.",
                    IsBlocking = false,
                    BlockingLabel = "Блокирует реальный запуск: отсутствует YOLO-модель"
                };
            }

            try
            {
                using var bitmap = SKBitmap.Decode(cameraProbe.SampleFrameBytes);
                using var yolo = new Yolo(new YoloOptions
                {
                    OnnxModel = modelPath,
                    ExecutionProvider = new CpuExecutionProvider()
                });

                var detections = yolo.RunObjectDetection(bitmap, confidence: 0.2, iou: 0.4);
                var personDetected = detections.Any(item => string.Equals(item.Label.Name, "person", StringComparison.OrdinalIgnoreCase));
                var labelSummary = detections.Count == 0
                    ? "объекты не обнаружены"
                    : string.Join(", ", detections.Take(5).Select(item => $"{item.Label.Name}:{item.Confidence:0.00}"));

                return new DeviceStatusSnapshot
                {
                    Id = "yolo",
                    DisplayName = "YOLO / presence",
                    State = DeviceReadinessState.Ready,
                    StatusLabel = personDetected ? "Person detected" : "Инференс выполнен",
                    Detail = $"Модель загружена, инференс выполнен по кадру камеры. Результат: {labelSummary}.",
                    IsBlocking = false
                };
            }
            catch (Exception ex)
            {
                return new DeviceStatusSnapshot
                {
                    Id = "yolo",
                    DisplayName = "YOLO / presence",
                    State = DeviceReadinessState.Error,
                    StatusLabel = "Ошибка инференса",
                    Detail = $"Не удалось выполнить YOLO inference: {ex.Message}",
                    IsBlocking = false,
                    BlockingLabel = "Блокирует реальный запуск: YOLO не запускается"
                };
            }
        }

        private DeviceStatusSnapshot ProbeComEnvironment(SerialPortProbeReport report, Esp32ControllerConfig? controller)
        {
            var configuredPort = controller?.ResolveConfiguredPort();
            var aliasText = report.Aliases.Count == 0
                ? "aliases не обнаружены"
                : string.Join(", ", report.Aliases.Select(item => $"{item.Key} -> {item.Value}"));
            var openedText = report.OpenedPorts.Count == 0 ? "не удалось открыть ни один порт" : string.Join(", ", report.OpenedPorts);

            if (report.DetectedPorts.Count == 0 && report.Aliases.Count == 0)
            {
                return new DeviceStatusSnapshot
                {
                    Id = "com",
                    DisplayName = "COM / контроллер",
                    State = DeviceReadinessState.Missing,
                    StatusLabel = "Порты не найдены",
                    Detail = $"Serial-порты не обнаружены системой. Настроенный порт: {configuredPort ?? "не задан"}.",
                    IsBlocking = true,
                    BlockingLabel = "Блокирует реальный запуск: контроллер не обнаружен"
                };
            }

            return new DeviceStatusSnapshot
            {
                Id = "com",
                DisplayName = "COM / контроллер",
                State = report.OpenedPorts.Count > 0 ? DeviceReadinessState.Ready : DeviceReadinessState.Warning,
                StatusLabel = report.OpenedPorts.Count > 0 ? "Порты обнаружены" : "Порты есть, но не открываются",
                Detail = $"Detected: {string.Join(", ", report.DetectedPorts.DefaultIfEmpty("нет"))}. Opened: {openedText}. Настроенный порт: {configuredPort ?? "не задан"}. {aliasText}.",
                IsBlocking = false
            };
        }

        private DeviceStatusSnapshot BuildControllerConnectionStatus(SerialPortProbeReport report, Esp32ControllerConfig? controller, ControllerPreflightResult controllerProbe, bool diagnosticsMode)
        {
            if (controller is null)
            {
                return new DeviceStatusSnapshot
                {
                    Id = "controller-connect",
                    DisplayName = "Подключение к контроллеру",
                    State = DeviceReadinessState.Missing,
                    StatusLabel = "Конфиг не задан",
                    Detail = $"Файл {ConfigFileName} не содержит раздел controller.",
                    IsBlocking = !diagnosticsMode,
                    BlockingLabel = "Блокирует реальный запуск: контроллер не настроен"
                };
            }

            var configuredPort = controller.ResolveConfiguredPort();
            if (!controllerProbe.PortOpened || string.IsNullOrWhiteSpace(controllerProbe.PortName))
            {
                return new DeviceStatusSnapshot
                {
                    Id = "controller-connect",
                    DisplayName = "Подключение к контроллеру",
                    State = DeviceReadinessState.Missing,
                    StatusLabel = "Порт не найден",
                    Detail = $"Ни один из путей контроллера не найден. Настроено: {configuredPort ?? "не задано"}. Обнаружено: {string.Join(", ", report.DetectedPorts.DefaultIfEmpty("нет"))}.",
                    IsBlocking = !diagnosticsMode,
                    BlockingLabel = "Блокирует реальный запуск: порт контроллера не найден"
                };
            }

            var resolvedPort = controllerProbe.PortName ?? configuredPort ?? string.Empty;
            var attempt = controllerProbe.PortOpened
                ? new SerialPortOpenAttempt(resolvedPort, true, null)
                : report.OpenAttempts.FirstOrDefault(item =>
                    string.Equals(item.PortName, resolvedPort, StringComparison.OrdinalIgnoreCase));

            if (controllerProbe.PortOpened && attempt is { Success: true })
            {
                return new DeviceStatusSnapshot
                {
                    Id = "controller-connect",
                    DisplayName = "Подключение к контроллеру",
                    State = DeviceReadinessState.Ready,
                    StatusLabel = "Порт открыт",
                    Detail = $"Контроллер привязан к {resolvedPort} и порт открывается на baudRate {controller.BaudRate}.",
                    IsBlocking = false
                };
            }

            var commandResolvedPort = controllerProbe.PortName ?? controller.ResolveConfiguredPort() ?? string.Empty;
            if (false && (!controllerProbe.CommandsSent || string.IsNullOrWhiteSpace(commandResolvedPort)))
            {
                return new DeviceStatusSnapshot
                {
                    Id = "controller-commands",
                    DisplayName = "Отправка команд",
                    State = DeviceReadinessState.Unavailable,
                    StatusLabel = "Команды не отправляются",
                    Detail = $"Preflight не подтвердил отправку x1x1x / x1x2x / x1x3x. {controllerProbe.ErrorMessage ?? "Причина не получена."}",
                    IsBlocking = !diagnosticsMode,
                    BlockingLabel = "Блокирует реальный запуск: команды ESP32 не отправляются"
                };
            }

            return new DeviceStatusSnapshot
            {
                Id = "controller-connect",
                DisplayName = "Подключение к контроллеру",
                State = DeviceReadinessState.Unavailable,
                StatusLabel = "Порт не открывается",
                Detail = $"Путь {resolvedPort} найден, но открыть его не удалось. {attempt?.ErrorMessage ?? "Причина не получена."}",
                IsBlocking = !diagnosticsMode,
                BlockingLabel = "Блокирует реальный запуск: контроллер недоступен"
            };
        }

        private DeviceStatusSnapshot BuildControllerCommandStatus(Esp32ControllerConfig? controller, ControllerPreflightResult controllerProbe, bool diagnosticsMode)
        {
            if (controller is null)
            {
                return new DeviceStatusSnapshot
                {
                    Id = "controller-commands",
                    DisplayName = "Отправка команд",
                    State = DeviceReadinessState.Skipped,
                    StatusLabel = "Пропущено до подключения",
                    Detail = "Команды x1x1x / x1x2x / x1x3x будут отправлены только после успешного открытия порта контроллера.",
                    IsBlocking = false
                };
            }

            var resolvedPort = controllerProbe.PortName ?? controller.ResolveConfiguredPort() ?? string.Empty;
            if (!controllerProbe.CommandsSent || string.IsNullOrWhiteSpace(resolvedPort))
            {
                return new DeviceStatusSnapshot
                {
                    Id = "controller-commands",
                    DisplayName = "Отправка команд",
                    State = DeviceReadinessState.Unavailable,
                    StatusLabel = "Команды не отправляются",
                    Detail = $"Preflight не подтвердил отправку x1x1x / x1x2x / x1x3x. {controllerProbe.ErrorMessage ?? "Причина не получена."}",
                    IsBlocking = !diagnosticsMode,
                    BlockingLabel = "Блокирует реальный запуск: команды ESP32 не отправляются"
                };
            }

            return new DeviceStatusSnapshot
            {
                Id = "controller-commands",
                DisplayName = "Отправка команд",
                State = DeviceReadinessState.Ready,
                StatusLabel = "Сценарий подготовлен",
                Detail = $"Основной сценарий готов отправить x1x1x, x1x2x, x1x3x через {resolvedPort} при baudRate {controller!.BaudRate}.",
                IsBlocking = false
            };
        }

        private DeviceStatusSnapshot BuildControllerDataStatus(ControllerPreflightResult controllerProbe)
        {
            var resolvedPort = controllerProbe.PortName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(resolvedPort))
            {
                return new DeviceStatusSnapshot
                {
                    Id = "controller-data",
                    DisplayName = "Получение JSON",
                    State = DeviceReadinessState.Skipped,
                    StatusLabel = "Пропущено до подключения",
                    Detail = "JSON {Temp,Alco,SYS,DAD} будет ожидаться после успешного подключения к контроллеру.",
                    IsBlocking = false
                };
            }

            return new DeviceStatusSnapshot
            {
                Id = "controller-data",
                DisplayName = "Получение JSON",
                State = DeviceReadinessState.Warning,
                StatusLabel = "Проверка ответа в runtime",
                Detail = $"Preflight подтвердил только порт {resolvedPort}. Реальный JSON-ответ контроллера проверяется в основном сценарии чтения.",
                IsBlocking = false
            };
        }

        private static bool IsControllerPortOpen(SerialPortProbeReport report, Esp32ControllerConfig? controller, out string resolvedPort)
        {
            resolvedPort = controller?.ResolvePort() ?? string.Empty;
            var portName = resolvedPort;
            return !string.IsNullOrWhiteSpace(resolvedPort)
                && report.OpenAttempts.Any(item =>
                    item.Success && string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase));
        }

        private ControllerPreflightResult ProbeController(Esp32ControllerConfig? controller, CancellationToken cancellationToken)
        {
            if (controller is null)
            {
                return new ControllerPreflightResult(null, false, false, null);
            }

            Exception? lastException = null;
            string? lastOpenedPort = null;

            foreach (var candidate in controller.EnumerateCandidatePorts())
            {
                cancellationToken.ThrowIfCancellationRequested();
                logStore.Info("Preflight", $"Preflight step started: controller connect {candidate}");
                try
                {
                    using var port = new SerialPort(candidate, controller.BaudRate)
                    {
                        ReadTimeout = controller.PreflightTimeoutMs,
                        WriteTimeout = controller.PreflightTimeoutMs
                    };

                    port.Open();
                    lastOpenedPort = candidate;
                    logStore.Info("Preflight", $"Open controller port: {candidate}");
                    port.Write("x1x1x");
                    Thread.Sleep(controller.PreflightCommandDelayMs);
                    cancellationToken.ThrowIfCancellationRequested();
                    port.Write("x1x2x");
                    Thread.Sleep(controller.PreflightCommandDelayMs);
                    cancellationToken.ThrowIfCancellationRequested();

                    logStore.Info("Preflight", "Preflight step completed: controller commands sent safely");
                    return new ControllerPreflightResult(candidate, true, true, null);
                }
                catch (OperationCanceledException)
                {
                    logStore.Warning("Preflight", "Measurement aborted for safety");
                    throw;
                }
                catch (TimeoutException ex)
                {
                    lastException = ex;
                    logStore.Warning("Preflight", $"Preflight step timeout: {candidate} ({ex.Message})");
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    logStore.Warning("Preflight", $"Preflight step failed: {candidate} ({ex.Message})");
                }
            }

            return new ControllerPreflightResult(
                lastOpenedPort ?? controller.ResolveConfiguredPort(),
                lastOpenedPort is not null,
                false,
                lastException?.Message);
        }

        private SerialPortProbeReport ProbeSerialPorts()
        {
            var detectedPorts = GetSerialPorts();
            var aliases = GetLinuxSerialAliases();
            var openAttempts = detectedPorts.Select(TryOpenPort).ToList();
            return new SerialPortProbeReport(detectedPorts, aliases, openAttempts);
        }

        private List<string> GetSerialPorts()
        {
            var ports = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
            if (OperatingSystem.IsLinux())
            {
                foreach (var prefix in new[] { "/dev/ttyUSB", "/dev/ttyACM" })
                {
                    foreach (var path in SafeEnumerate(prefix + "*"))
                    {
                        ports.Add(path);
                    }
                }
            }

            return ports.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IReadOnlyList<string> SafeEnumerate(string pattern)
        {
            try
            {
                var directory = Path.GetDirectoryName(pattern);
                var filePattern = Path.GetFileName(pattern);
                return string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)
                    ? Array.Empty<string>()
                    : Directory.EnumerateFiles(directory, filePattern).OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static IReadOnlyDictionary<string, string> GetLinuxSerialAliases()
        {
            if (!OperatingSystem.IsLinux())
            {
                return new Dictionary<string, string>();
            }

            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var basePath in new[] { "/dev/serial/by-id", "/dev/serial/by-path" })
            {
                if (!Directory.Exists(basePath))
                {
                    continue;
                }

                foreach (var entry in Directory.EnumerateFileSystemEntries(basePath))
                {
                    try
                    {
                        aliases[entry] = File.ResolveLinkTarget(entry, false)?.FullName ?? entry;
                    }
                    catch
                    {
                        aliases[entry] = entry;
                    }
                }
            }

            return aliases;
        }

        private static SerialPortOpenAttempt TryOpenPort(string portName)
        {
            try
            {
                using var port = new SerialPort(portName)
                {
                    ReadTimeout = 500,
                    WriteTimeout = 500
                };

                port.Open();
                return new SerialPortOpenAttempt(portName, true, null);
            }
            catch (Exception ex)
            {
                return new SerialPortOpenAttempt(portName, false, ex.Message);
            }
        }

        private DevicePortMapConfig TryLoadConfig()
        {
            var path = Path.Combine(baseDirectory, ConfigFileName);
            if (!File.Exists(path))
            {
                return new DevicePortMapConfig();
            }

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<DevicePortMapConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new DevicePortMapConfig();
            }
            catch
            {
                return new DevicePortMapConfig();
            }
        }

        private DeviceStatusSnapshot BuildControllerSmokeStatus(string resolvedPort, string rawResponse)
        {
            var normalized = NormalizeControllerJson(rawResponse);
            using var document = JsonDocument.Parse(normalized);
            var root = document.RootElement;
            var keys = new[] { "Temp", "Alco", "SYS", "DAD" };
            var missing = keys.Where(key => !root.TryGetProperty(key, out _)).ToArray();

            return missing.Length == 0
                ? new DeviceStatusSnapshot
                {
                    Id = "controller-data",
                    DisplayName = "Получение JSON",
                    State = DeviceReadinessState.Ready,
                    StatusLabel = "JSON подтверждён",
                    Detail = $"Контроллер {resolvedPort} вернул JSON с полями Temp, Alco, SYS, DAD.",
                    IsBlocking = false
                }
                : new DeviceStatusSnapshot
                {
                    Id = "controller-data",
                    DisplayName = "Получение JSON",
                    State = DeviceReadinessState.Warning,
                    StatusLabel = "JSON неполный",
                    Detail = $"Контроллер {resolvedPort} ответил JSON, но отсутствуют поля: {string.Join(", ", missing)}.",
                    IsBlocking = false
                };
        }

        private static string NormalizeControllerJson(string payload)
        {
            return Regex.Replace(payload.Trim(), @"(?<=[{,])\s*(Temp|Alco|SYS|DAD)\s*:", "\"$1\":");
        }

        private static string BuildCameraEnvironmentDetail(IReadOnlyList<string> deviceList, string? selectedDevice, string? permissionNote)
        {
            var parts = new List<string>
            {
                $"Detected: {(deviceList.Count == 0 ? "нет video-устройств" : string.Join(", ", deviceList))}",
                $"Selected: {selectedDevice ?? "не выбрано"}"
            };

            if (!string.IsNullOrWhiteSpace(permissionNote))
            {
                parts.Add(permissionNote);
            }

            return string.Join(". ", parts) + ".";
        }

        private static List<string> GetLinuxVideoDevices()
        {
            if (!OperatingSystem.IsLinux())
            {
                return new List<string>();
            }

            return SafeEnumerate("/dev/video*").ToList();
        }

        private static string? GetPrimaryLinuxVideoDevice(IReadOnlyList<string> devices)
        {
            if (!OperatingSystem.IsLinux())
            {
                return null;
            }

            return devices.FirstOrDefault(item => string.Equals(item, LinuxPrimaryVideoDevice, StringComparison.OrdinalIgnoreCase));
        }

        private static string? GetLinuxCameraPermissionNote()
        {
            if (!OperatingSystem.IsLinux())
            {
                return null;
            }

            try
            {
                var groups = File.ReadAllLines("/etc/group");
                var videoGroup = groups.FirstOrDefault(line => line.StartsWith("video:", StringComparison.Ordinal));
                return videoGroup is null ? "Группа video не найдена в системе." : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsPermissionError(Exception exception)
        {
            var message = exception.Message.ToLowerInvariant();
            return message.Contains("permission")
                || message.Contains("access denied")
                || message.Contains("denied")
                || message.Contains("eacces");
        }

        private static SKBitmap? TryConvertMatToSkBitmap(Mat frame)
        {
            try
            {
                using var converted = new Mat();
                if (frame.NumberOfChannels == 4)
                {
                    CvInvoke.CvtColor(frame, converted, ColorConversion.Bgra2Rgba);
                }
                else if (frame.NumberOfChannels == 3)
                {
                    CvInvoke.CvtColor(frame, converted, ColorConversion.Bgr2Rgba);
                }
                else if (frame.NumberOfChannels == 1)
                {
                    CvInvoke.CvtColor(frame, converted, ColorConversion.Gray2Rgba);
                }
                else
                {
                    return null;
                }

                var bytes = new byte[converted.Rows * converted.Cols * converted.NumberOfChannels];
                Marshal.Copy(converted.DataPointer, bytes, 0, bytes.Length);
                var bitmap = new SKBitmap(converted.Cols, converted.Rows, SKColorType.Rgba8888, SKAlphaType.Opaque);
                Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private static SKBitmap ApplyCameraRotation(SKBitmap source, int cameraRotation)
        {
            if (cameraRotation != 180)
            {
                return source.Copy();
            }

            var rotated = new SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType);
            using var canvas = new SKCanvas(rotated);
            canvas.Translate(source.Width, source.Height);
            canvas.RotateDegrees(180);
            canvas.DrawBitmap(source, 0, 0);
            canvas.Flush();
            return rotated;
        }

        private sealed record CameraProbeResult(DeviceStatusSnapshot Status, byte[]? SampleFrameBytes = null);

        private sealed record SerialPortProbeReport(
            IReadOnlyList<string> DetectedPorts,
            IReadOnlyDictionary<string, string> Aliases,
            IReadOnlyList<SerialPortOpenAttempt> OpenAttempts)
        {
            public IReadOnlyList<string> OpenedPorts =>
                OpenAttempts.Where(item => item.Success).Select(item => item.PortName).ToArray();
        }

        private sealed record ControllerPreflightResult(string? PortName, bool PortOpened, bool CommandsSent, string? ErrorMessage);

        private sealed record SerialPortOpenAttempt(string PortName, bool Success, string? ErrorMessage);

        private sealed class DevicePortMapConfig
        {
            [JsonPropertyName("cameraRotation")]
            public int CameraRotation { get; init; }

            [JsonPropertyName("linuxNoCameraMode")]
            public bool LinuxNoCameraMode { get; init; }

            [JsonPropertyName("controller")]
            public Esp32ControllerConfig? Controller { get; init; }
        }

        private sealed class Esp32ControllerConfig
        {
            [JsonPropertyName("port")]
            public string? Port { get; init; }

            [JsonPropertyName("portCandidates")]
            public string[]? PortCandidates { get; init; }

            [JsonPropertyName("baudRate")]
            public int BaudRate { get; init; } = 115200;
            public int PreflightTimeoutMs { get; init; } = 1500;
            public int PreflightCommandDelayMs { get; init; } = 75;

            public string? ResolveConfiguredPort() =>
                EnumerateCandidatePorts().FirstOrDefault();

            public string? ResolvePort() =>
                EnumerateCandidatePorts().FirstOrDefault(path =>
                    !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || SerialPort.GetPortNames().Contains(path, StringComparer.OrdinalIgnoreCase)));

            public IEnumerable<string> EnumerateCandidatePorts()
            {
                var candidates = new List<string>();
                if (!string.IsNullOrWhiteSpace(Port))
                {
                    candidates.Add(Port);
                }

                if (PortCandidates is not null)
                {
                    candidates.AddRange(PortCandidates.Where(item => !string.IsNullOrWhiteSpace(item)));
                }

                IEnumerable<string> orderedCandidates = candidates.Distinct(StringComparer.OrdinalIgnoreCase);
                if (OperatingSystem.IsLinux())
                {
                    orderedCandidates = orderedCandidates
                        .OrderBy(GetLinuxPriority)
                        .ThenBy(item => item, StringComparer.OrdinalIgnoreCase);
                }

                foreach (var candidate in orderedCandidates)
                {
                    yield return candidate;
                }
            }

            private static int GetLinuxPriority(string portName)
            {
                if (string.Equals(portName, "/dev/ttyUSB0", StringComparison.OrdinalIgnoreCase))
                {
                    return 0;
                }

                if (portName.StartsWith("/dev/ttyUSB", StringComparison.OrdinalIgnoreCase))
                {
                    return 1;
                }

                if (portName.StartsWith("/dev/ttyACM", StringComparison.OrdinalIgnoreCase))
                {
                    return 2;
                }

                if (portName.Contains("/dev/serial/by-id/", StringComparison.OrdinalIgnoreCase))
                {
                    return 3;
                }

                return 4;
            }
        }
    }
}
