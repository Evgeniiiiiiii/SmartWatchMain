using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Emgu.CV;
using Emgu.CV.Util;
using SkiaSharp;
using SmartWatchProj.Models.Devices;
using YoloDotNet;
using YoloDotNet.Core;
using YoloDotNet.Models;

namespace SmartWatchProj.Services.Devices
{
    public sealed class DevicePreflightService
    {
        private const int CameraIndex = 0;
        private readonly string baseDirectory;

        public DevicePreflightService(string baseDirectory)
        {
            this.baseDirectory = baseDirectory;
        }

        public IReadOnlyList<DeviceStatusSnapshot> Check(bool diagnosticsMode)
        {
            var serialReport = ProbeSerialPorts();
            var comPlans = BuildComModulePlans(serialReport);
            var cameraProbe = ProbeCamera(diagnosticsMode);

            var statuses = new List<DeviceStatusSnapshot>
            {
                cameraProbe.Status,
                ProbeYoloModel(cameraProbe, diagnosticsMode),
                ProbeComEnvironment(serialReport, comPlans)
            };

            statuses.AddRange(comPlans.Select(plan => BuildPeripheralStatus(diagnosticsMode, serialReport, plan)));
            return statuses;
        }

        private CameraProbeResult ProbeCamera(bool diagnosticsMode)
        {
            try
            {
                using var probe = new VideoCapture(CameraIndex);
                if (!probe.IsOpened)
                {
                    return new CameraProbeResult(new DeviceStatusSnapshot
                    {
                        Id = "camera",
                        DisplayName = "Камера",
                        State = DeviceReadinessState.Missing,
                        Detail = $"Камера с индексом {CameraIndex} не открылась.",
                        IsBlocking = !diagnosticsMode
                    });
                }

                using var frame = new Mat();
                for (var attempt = 1; attempt <= 4; attempt++)
                {
                    if (probe.Read(frame) && !frame.IsEmpty)
                    {
                        using var buffer = new VectorOfByte();
                        CvInvoke.Imencode(".jpg", frame, buffer);
                        var frameBytes = buffer.ToArray();
                        if (frameBytes.Length > 0)
                        {
                            return new CameraProbeResult(
                                new DeviceStatusSnapshot
                                {
                                    Id = "camera",
                                    DisplayName = "Камера",
                                    State = DeviceReadinessState.Ready,
                                    Detail = $"Камера {CameraIndex} открылась и отдала кадр (попытка {attempt}).",
                                    IsBlocking = false
                                },
                                frameBytes);
                        }
                    }

                    Thread.Sleep(150);
                }

                return new CameraProbeResult(new DeviceStatusSnapshot
                {
                    Id = "camera",
                    DisplayName = "Камера",
                    State = DeviceReadinessState.Unavailable,
                    Detail = $"Камера {CameraIndex} открылась, но ни один кадр не был получен.",
                    IsBlocking = !diagnosticsMode
                });
            }
            catch (Exception ex)
            {
                return new CameraProbeResult(new DeviceStatusSnapshot
                {
                    Id = "camera",
                    DisplayName = "Камера",
                    State = DeviceReadinessState.Error,
                    Detail = $"Ошибка открытия камеры {CameraIndex}: {ex.Message}",
                    IsBlocking = !diagnosticsMode
                });
            }
        }

        private DeviceStatusSnapshot ProbeYoloModel(CameraProbeResult cameraProbe, bool diagnosticsMode)
        {
            if (cameraProbe.Status.State != DeviceReadinessState.Ready || cameraProbe.FrameBytes is null || cameraProbe.FrameBytes.Length == 0)
            {
                return new DeviceStatusSnapshot
                {
                    Id = "yolo",
                    DisplayName = "YOLO / модель присутствия",
                    State = DeviceReadinessState.Skipped,
                    Detail = $"Проверка пропущена: камера не подтверждена ({cameraProbe.Status.StateText.ToLowerInvariant()}).",
                    IsBlocking = false
                };
            }

            var modelPath = Path.Combine(baseDirectory, "Assets", "yolov8n.onnx");
            if (!File.Exists(modelPath))
            {
                return new DeviceStatusSnapshot
                {
                    Id = "yolo",
                    DisplayName = "YOLO / модель присутствия",
                    State = DeviceReadinessState.Missing,
                    Detail = $"Файл модели не найден: {modelPath}",
                    IsBlocking = !diagnosticsMode
                };
            }

            try
            {
                var skBitmap = SKBitmap.Decode(cameraProbe.FrameBytes);
                if (skBitmap is null)
                {
                    return new DeviceStatusSnapshot
                    {
                        Id = "yolo",
                        DisplayName = "YOLO / модель присутствия",
                        State = DeviceReadinessState.Error,
                        Detail = "Тестовый кадр камеры не удалось декодировать для проверки YOLO.",
                        IsBlocking = !diagnosticsMode
                    };
                }

                using (skBitmap)
                {
                    var probe = new Yolo(new YoloOptions
                    {
                        OnnxModel = modelPath,
                        ExecutionProvider = new CpuExecutionProvider()
                    });

                    try
                    {
                        var results = probe.RunObjectDetection(skBitmap, confidence: 0.5, iou: 0.45);
                        var personCount = results.Count(result =>
                            string.Equals(result.Label.Name, "person", StringComparison.OrdinalIgnoreCase)
                            && result.Confidence > 0.5);

                        return new DeviceStatusSnapshot
                        {
                            Id = "yolo",
                            DisplayName = "YOLO / модель присутствия",
                            State = DeviceReadinessState.Ready,
                            Detail = $"Модель загружена и выполнила инференс на тестовом кадре. Обнаружено person: {personCount}.",
                            IsBlocking = false
                        };
                    }
                    finally
                    {
                        if (probe is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return new DeviceStatusSnapshot
                {
                    Id = "yolo",
                    DisplayName = "YOLO / модель присутствия",
                    State = DeviceReadinessState.Error,
                    Detail = $"Ошибка проверки YOLO: {ex.Message}",
                    IsBlocking = !diagnosticsMode
                };
            }
        }

        private static DeviceStatusSnapshot ProbeComEnvironment(
            SerialPortProbeReport report,
            IReadOnlyList<ComModuleProbePlan> plans)
        {
            if (report.ProbeError is not null)
            {
                return new DeviceStatusSnapshot
                {
                    Id = "com",
                    DisplayName = "COM / последовательные порты",
                    State = DeviceReadinessState.Error,
                    StatusLabel = "Ошибка COM",
                    Detail = $"Не удалось получить список COM-портов: {report.ProbeError}",
                    IsBlocking = false
                };
            }

            if (report.DetectedPorts.Length == 0)
            {
                return new DeviceStatusSnapshot
                {
                    Id = "com",
                    DisplayName = "COM / последовательные порты",
                    State = DeviceReadinessState.Missing,
                    StatusLabel = "Порты не найдены",
                    Detail = "COM-порты не обнаружены системой.",
                    IsBlocking = false
                };
            }

            var details = new List<string>
            {
                $"Найдены системой: {string.Join(", ", report.DetectedPorts)}."
            };

            if (report.AccessiblePorts.Length > 0)
            {
                details.Add($"Открываются: {string.Join(", ", report.AccessiblePorts)}.");
            }

            if (report.InaccessiblePorts.Length > 0)
            {
                details.Add($"Не открываются: {string.Join("; ", report.InaccessiblePorts)}.");
            }

            details.Add(BuildAssignmentsSummary(plans));

            if (report.AccessiblePorts.Length == 0)
            {
                return new DeviceStatusSnapshot
                {
                    Id = "com",
                    DisplayName = "COM / последовательные порты",
                    State = DeviceReadinessState.Unavailable,
                    StatusLabel = "Порты недоступны",
                    Detail = string.Join(" ", details),
                    IsBlocking = false
                };
            }

            var hasImplementedHandshake = plans.Any(plan => plan.SmokeTest.IsImplemented);
            details.Add(hasImplementedHandshake
                ? "Handshake будет выполняться только для модулей с настроенным smoke-test."
                : "Handshake для COM-модулей пока не настроен.");

            return new DeviceStatusSnapshot
            {
                Id = "com",
                DisplayName = "COM / последовательные порты",
                State = DeviceReadinessState.Warning,
                StatusLabel = "Порты обнаружены",
                Detail = string.Join(" ", details),
                IsBlocking = false
            };
        }

        private static DeviceStatusSnapshot BuildPeripheralStatus(
            bool diagnosticsMode,
            SerialPortProbeReport report,
            ComModuleProbePlan plan)
        {
            if (report.ProbeError is not null)
            {
                return new DeviceStatusSnapshot
                {
                    Id = plan.Id,
                    DisplayName = plan.DisplayName,
                    State = DeviceReadinessState.Error,
                    StatusLabel = "Ошибка COM",
                    Detail = $"COM-окружение недоступно: {report.ProbeError}. {plan.IntegrationNote}",
                    IsBlocking = !diagnosticsMode
                };
            }

            if (string.IsNullOrWhiteSpace(plan.AssignedPort))
            {
                var detail = plan.CandidatePort is null
                    ? $"Для модуля '{plan.DisplayName}' порт не назначен. {plan.IntegrationNote}"
                    : $"Для модуля '{plan.DisplayName}' порт не назначен. Кандидат для проверки: {plan.CandidatePort}. {plan.IntegrationNote}";

                return new DeviceStatusSnapshot
                {
                    Id = plan.Id,
                    DisplayName = plan.DisplayName,
                    State = diagnosticsMode ? DeviceReadinessState.Diagnostics : DeviceReadinessState.Missing,
                    StatusLabel = "Порт не назначен",
                    Detail = detail,
                    IsBlocking = !diagnosticsMode,
                    BlockingLabel = diagnosticsMode
                        ? "Доступен только diagnostics fallback"
                        : "Блокирует реальный запуск: сначала назначьте COM-порт"
                };
            }

            var portResult = report.FindPort(plan.AssignedPort);
            if (portResult is null)
            {
                return new DeviceStatusSnapshot
                {
                    Id = plan.Id,
                    DisplayName = plan.DisplayName,
                    State = DeviceReadinessState.Missing,
                    StatusLabel = "Порт не найден",
                    Detail = $"Для модуля '{plan.DisplayName}' назначен порт {plan.AssignedPort}, но система его сейчас не видит. {plan.IntegrationNote}",
                    IsBlocking = !diagnosticsMode
                };
            }

            if (!portResult.IsAccessible)
            {
                return new DeviceStatusSnapshot
                {
                    Id = plan.Id,
                    DisplayName = plan.DisplayName,
                    State = DeviceReadinessState.Unavailable,
                    StatusLabel = "Порт недоступен",
                    Detail = $"Для модуля '{plan.DisplayName}' назначенный порт {plan.AssignedPort} найден, но не открывается: {portResult.OpenError}. {plan.IntegrationNote}",
                    IsBlocking = !diagnosticsMode
                };
            }

            var smokeResult = RunSmokeTest(plan.SmokeTest, plan.AssignedPort);
            var baseDetail = $"Для модуля '{plan.DisplayName}' назначенный порт {plan.AssignedPort} найден и открывается.";

            return smokeResult.Outcome switch
            {
                SmokeTestOutcome.NotImplemented => new DeviceStatusSnapshot
                {
                    Id = plan.Id,
                    DisplayName = plan.DisplayName,
                    State = diagnosticsMode ? DeviceReadinessState.Diagnostics : DeviceReadinessState.Warning,
                    StatusLabel = "Handshake не реализован",
                    Detail = $"{baseDetail} Протокол устройства ещё не проверен: handshake для этого типа модуля ещё не реализован. {plan.IntegrationNote}",
                    IsBlocking = !diagnosticsMode,
                    BlockingLabel = diagnosticsMode
                        ? "Доступен только diagnostics fallback"
                        : "Блокирует реальный запуск: устройство не подтверждено"
                },
                SmokeTestOutcome.NoResponse => new DeviceStatusSnapshot
                {
                    Id = plan.Id,
                    DisplayName = plan.DisplayName,
                    State = diagnosticsMode ? DeviceReadinessState.Diagnostics : DeviceReadinessState.Warning,
                    StatusLabel = "Handshake не выполнен",
                    Detail = $"{baseDetail} Ответ от устройства не получен. {smokeResult.Detail} {plan.IntegrationNote}",
                    IsBlocking = !diagnosticsMode,
                    BlockingLabel = diagnosticsMode
                        ? "Доступен только diagnostics fallback"
                        : "Блокирует реальный запуск: устройство не подтверждено"
                },
                SmokeTestOutcome.Unrecognized => new DeviceStatusSnapshot
                {
                    Id = plan.Id,
                    DisplayName = plan.DisplayName,
                    State = diagnosticsMode ? DeviceReadinessState.Diagnostics : DeviceReadinessState.Warning,
                    StatusLabel = "Устройство не распознано",
                    Detail = $"{baseDetail} Handshake выполнен, но ответ не совпал с ожидаемым протоколом. {smokeResult.Detail} {plan.IntegrationNote}",
                    IsBlocking = !diagnosticsMode,
                    BlockingLabel = diagnosticsMode
                        ? "Доступен только diagnostics fallback"
                        : "Блокирует реальный запуск: устройство не подтверждено"
                },
                SmokeTestOutcome.Confirmed when !plan.ProviderImplemented => new DeviceStatusSnapshot
                {
                    Id = plan.Id,
                    DisplayName = plan.DisplayName,
                    State = diagnosticsMode ? DeviceReadinessState.Diagnostics : DeviceReadinessState.Warning,
                    StatusLabel = "Устройство подтверждено",
                    Detail = $"{baseDetail} Handshake выполнен успешно. Устройство подтверждено, но модуль измерения ещё не подключен к real provider. {smokeResult.Detail}",
                    IsBlocking = !diagnosticsMode,
                    BlockingLabel = diagnosticsMode
                        ? "Доступен только diagnostics fallback"
                        : "Блокирует реальный запуск: нет real provider"
                },
                SmokeTestOutcome.Confirmed => new DeviceStatusSnapshot
                {
                    Id = plan.Id,
                    DisplayName = plan.DisplayName,
                    State = DeviceReadinessState.Ready,
                    StatusLabel = "Модуль готов",
                    Detail = $"{baseDetail} Handshake выполнен успешно, модуль подтвержден как готовый. {smokeResult.Detail}",
                    IsBlocking = false
                },
                SmokeTestOutcome.Error => new DeviceStatusSnapshot
                {
                    Id = plan.Id,
                    DisplayName = plan.DisplayName,
                    State = diagnosticsMode ? DeviceReadinessState.Diagnostics : DeviceReadinessState.Error,
                    StatusLabel = "Ошибка handshake",
                    Detail = $"{baseDetail} Ошибка smoke-test: {smokeResult.Detail} {plan.IntegrationNote}",
                    IsBlocking = !diagnosticsMode,
                    BlockingLabel = diagnosticsMode
                        ? "Доступен только diagnostics fallback"
                        : "Блокирует реальный запуск: smoke-test завершился ошибкой"
                },
                _ => new DeviceStatusSnapshot
                {
                    Id = plan.Id,
                    DisplayName = plan.DisplayName,
                    State = diagnosticsMode ? DeviceReadinessState.Diagnostics : DeviceReadinessState.Warning,
                    StatusLabel = "Diagnostics fallback",
                    Detail = $"{baseDetail} Реальная готовность не подтверждена. {plan.IntegrationNote}",
                    IsBlocking = !diagnosticsMode
                }
            };
        }

        private IReadOnlyList<ComModuleProbePlan> BuildComModulePlans(SerialPortProbeReport report)
        {
            var moduleDefinitions = new[]
            {
                new ComModuleDefinition("watch", "Часы / носимое устройство", "Нужен конкретный COM-протокол или SDK устройства."),
                new ComModuleDefinition("thermometer", "Термометр", "Ожидается явная привязка к драйверу или COM-модулю."),
                new ComModuleDefinition("breathalyzer", "Алкотестер", "Нужен текстовый или JSON-протокол поверх SerialPort."),
                new ComModuleDefinition("blood-pressure", "Тонометр", "Нужен отдельный device provider вместо baseline-симуляции."),
                new ComModuleDefinition("glucose-meter", "Глюкометр", "Нужен provider для реального получения значения.")
            };

            var config = TryLoadComAssignments();
            var candidatePort = PickDefaultCandidatePort(report);

            return moduleDefinitions
                .Select(definition =>
                {
                    config.Modules.TryGetValue(definition.Id, out var moduleConfig);
                    var assignedPort = NormalizePortName(moduleConfig?.Port);
                    var smokeTest = BuildSmokeTest(moduleConfig);
                    var candidate = assignedPort ?? ResolveCandidatePort(definition.Id, candidatePort, report);

                    return new ComModuleProbePlan(
                        definition.Id,
                        definition.DisplayName,
                        definition.IntegrationNote,
                        assignedPort,
                        candidate,
                        ProviderImplemented: false,
                        SmokeTest: smokeTest);
                })
                .ToArray();
        }

        private ComAssignmentsConfig TryLoadComAssignments()
        {
            var configPath = Path.Combine(baseDirectory, "device-port-map.json");
            if (!File.Exists(configPath))
            {
                return ComAssignmentsConfig.Empty;
            }

            try
            {
                var json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<ComAssignmentsConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                return config ?? ComAssignmentsConfig.Empty;
            }
            catch
            {
                return ComAssignmentsConfig.Empty;
            }
        }

        private static SerialPortProbeReport ProbeSerialPorts()
        {
            var ports = GetSerialPorts(out var error);
            if (error is not null)
            {
                return new SerialPortProbeReport(Array.Empty<SerialPortProbeResult>(), error);
            }

            var results = ports
                .Select(portName =>
                {
                    var openError = TryOpenPort(portName);
                    return new SerialPortProbeResult(portName, openError is null, openError);
                })
                .ToArray();

            return new SerialPortProbeReport(results, null);
        }

        private static SmokeTestResult RunSmokeTest(ComSmokeTestDefinition smokeTest, string portName)
        {
            if (!smokeTest.IsImplemented)
            {
                return SmokeTestResult.NotImplemented();
            }

            if (!string.Equals(smokeTest.Protocol, "serial-text", StringComparison.OrdinalIgnoreCase))
            {
                return SmokeTestResult.NotImplemented(
                    $"Протокол '{smokeTest.Protocol}' пока не поддержан в preflight.");
            }

            try
            {
                using var port = new SerialPort(portName, smokeTest.BaudRate)
                {
                    ReadTimeout = smokeTest.TimeoutMs,
                    WriteTimeout = smokeTest.TimeoutMs,
                    NewLine = smokeTest.NewLine,
                    DtrEnable = false,
                    RtsEnable = false
                };

                port.Open();

                if (!string.IsNullOrEmpty(smokeTest.Message))
                {
                    if (smokeTest.RawWrite)
                    {
                        port.Write(smokeTest.Message);
                    }
                    else
                    {
                        port.WriteLine(smokeTest.Message);
                    }
                }

                Thread.Sleep(Math.Min(smokeTest.TimeoutMs, 250));

                string response;
                try
                {
                    response = smokeTest.ReadLine ? port.ReadLine() : port.ReadExisting();
                }
                catch (TimeoutException)
                {
                    response = string.Empty;
                }

                if (string.IsNullOrWhiteSpace(response))
                {
                    return new SmokeTestResult(
                        SmokeTestOutcome.NoResponse,
                        $"Smoke-test отправил '{smokeTest.Message}', но ответ пустой.");
                }

                if (!string.IsNullOrWhiteSpace(smokeTest.ExpectedResponseContains)
                    && response.IndexOf(smokeTest.ExpectedResponseContains, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return new SmokeTestResult(
                        SmokeTestOutcome.Unrecognized,
                        $"Получен ответ '{response}', ожидался фрагмент '{smokeTest.ExpectedResponseContains}'.");
                }

                return new SmokeTestResult(
                    SmokeTestOutcome.Confirmed,
                    $"Smoke-test получил ответ '{response}'.");
            }
            catch (Exception ex)
            {
                return new SmokeTestResult(SmokeTestOutcome.Error, ex.Message);
            }
        }

        private static ComSmokeTestDefinition BuildSmokeTest(ComModulePortConfig? config)
        {
            if (config is null || string.IsNullOrWhiteSpace(config.SmokeProtocol))
            {
                return ComSmokeTestDefinition.NotImplemented;
            }

            return new ComSmokeTestDefinition(
                config.SmokeProtocol.Trim(),
                config.BaudRate ?? 9600,
                config.Message ?? "PING",
                config.TimeoutMs ?? 1500,
                config.ReadLine ?? false,
                config.RawWrite ?? false,
                DecodeEscapes(config.NewLine ?? "\\n"),
                config.ExpectedResponseContains);
        }

        private static string? ResolveCandidatePort(string deviceId, string? defaultCandidatePort, SerialPortProbeReport report)
        {
            if (defaultCandidatePort is not null)
            {
                return defaultCandidatePort;
            }

            if (report.DetectedPorts.Length == 0)
            {
                return null;
            }

            return deviceId == "watch"
                ? report.DetectedPorts.LastOrDefault()
                : report.DetectedPorts.FirstOrDefault();
        }

        private static string? PickDefaultCandidatePort(SerialPortProbeReport report)
        {
            var preferred = report.AccessiblePorts
                .Where(port => !string.Equals(port, "COM1", StringComparison.OrdinalIgnoreCase))
                .OrderBy(port => port, StringComparer.OrdinalIgnoreCase)
                .LastOrDefault();

            return preferred
                ?? report.AccessiblePorts.LastOrDefault()
                ?? report.DetectedPorts
                    .Where(port => !string.Equals(port, "COM1", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(port => port, StringComparer.OrdinalIgnoreCase)
                    .LastOrDefault()
                ?? report.DetectedPorts.LastOrDefault();
        }

        private static string BuildAssignmentsSummary(IReadOnlyList<ComModuleProbePlan> plans)
        {
            var parts = plans.Select(plan =>
            {
                if (!string.IsNullOrWhiteSpace(plan.AssignedPort))
                {
                    return $"{plan.DisplayName} -> {plan.AssignedPort}";
                }

                if (!string.IsNullOrWhiteSpace(plan.CandidatePort))
                {
                    return $"{plan.DisplayName} -> кандидат {plan.CandidatePort}";
                }

                return $"{plan.DisplayName} -> не назначен";
            });

            return $"Модули: {string.Join("; ", parts)}.";
        }

        private static string? NormalizePortName(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim().ToUpperInvariant();

        private static string DecodeEscapes(string value) =>
            value
                .Replace("\\r", "\r", StringComparison.Ordinal)
                .Replace("\\n", "\n", StringComparison.Ordinal)
                .Replace("\\t", "\t", StringComparison.Ordinal);

        private static string? TryOpenPort(string portName)
        {
            try
            {
                using var port = new SerialPort(portName, 9600)
                {
                    ReadTimeout = 250,
                    WriteTimeout = 250,
                    DtrEnable = false,
                    RtsEnable = false
                };

                port.Open();
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static string[] GetSerialPorts(out string? error)
        {
            try
            {
                error = null;
                return SerialPort.GetPortNames()
                    .OrderBy(port => port, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return Array.Empty<string>();
            }
        }

        private sealed record CameraProbeResult(DeviceStatusSnapshot Status, byte[]? FrameBytes = null);
        private sealed record ComModuleDefinition(string Id, string DisplayName, string IntegrationNote);
        private sealed record ComModuleProbePlan(
            string Id,
            string DisplayName,
            string IntegrationNote,
            string? AssignedPort,
            string? CandidatePort,
            bool ProviderImplemented,
            ComSmokeTestDefinition SmokeTest);
        private sealed record SerialPortProbeResult(string PortName, bool IsAccessible, string? OpenError);

        private sealed record SerialPortProbeReport(SerialPortProbeResult[] PortResults, string? ProbeError)
        {
            public string[] DetectedPorts => PortResults.Select(result => result.PortName).ToArray();
            public string[] AccessiblePorts => PortResults.Where(result => result.IsAccessible).Select(result => result.PortName).ToArray();
            public string[] InaccessiblePorts => PortResults.Where(result => !result.IsAccessible).Select(result => $"{result.PortName} ({result.OpenError})").ToArray();

            public SerialPortProbeResult? FindPort(string portName) =>
                PortResults.FirstOrDefault(result =>
                    string.Equals(result.PortName, portName, StringComparison.OrdinalIgnoreCase));
        }

        private sealed record ComSmokeTestDefinition(
            string Protocol,
            int BaudRate,
            string Message,
            int TimeoutMs,
            bool ReadLine,
            bool RawWrite,
            string NewLine,
            string? ExpectedResponseContains)
        {
            public static readonly ComSmokeTestDefinition NotImplemented =
                new("none", 9600, "PING", 1500, false, false, "\n", null);

            public bool IsImplemented =>
                !string.Equals(Protocol, "none", StringComparison.OrdinalIgnoreCase);
        }

        private sealed record SmokeTestResult(SmokeTestOutcome Outcome, string Detail)
        {
            public static SmokeTestResult NotImplemented(string? detail = null) =>
                new(SmokeTestOutcome.NotImplemented, detail ?? "Handshake для этого модуля ещё не реализован.");
        }

        private enum SmokeTestOutcome
        {
            NotImplemented,
            NoResponse,
            Unrecognized,
            Confirmed,
            Error
        }

        private sealed class ComAssignmentsConfig
        {
            public static ComAssignmentsConfig Empty { get; } = new();

            public Dictionary<string, ComModulePortConfig> Modules { get; init; } =
                new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class ComModulePortConfig
        {
            public string? Port { get; init; }
            public string? SmokeProtocol { get; init; }
            public int? BaudRate { get; init; }
            public string? Message { get; init; }
            public int? TimeoutMs { get; init; }
            public bool? ReadLine { get; init; }
            public bool? RawWrite { get; init; }
            public string? NewLine { get; init; }
            public string? ExpectedResponseContains { get; init; }
        }
    }
}
