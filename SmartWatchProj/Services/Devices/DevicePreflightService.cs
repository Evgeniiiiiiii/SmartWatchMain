using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
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
            var cameraProbe = ProbeCamera(diagnosticsMode);

            var statuses = new List<DeviceStatusSnapshot>
            {
                cameraProbe.Status,
                ProbeYoloModel(cameraProbe, diagnosticsMode),
                ProbeComEnvironment(serialReport),
                BuildPeripheralStatus(
                    "watch",
                    "Часы / носимое устройство",
                    diagnosticsMode,
                    serialReport,
                    "Нужен конкретный COM-протокол или SDK устройства."),
                BuildPeripheralStatus(
                    "thermometer",
                    "Термометр",
                    diagnosticsMode,
                    serialReport,
                    "Ожидается явная привязка к драйверу или COM-модулю."),
                BuildPeripheralStatus(
                    "breathalyzer",
                    "Алкотестер",
                    diagnosticsMode,
                    serialReport,
                    "Нужен текстовый или JSON-протокол поверх SerialPort."),
                BuildPeripheralStatus(
                    "blood-pressure",
                    "Тонометр",
                    diagnosticsMode,
                    serialReport,
                    "Нужен отдельный device provider вместо baseline-симуляции."),
                BuildPeripheralStatus(
                    "glucose-meter",
                    "Глюкометр",
                    diagnosticsMode,
                    serialReport,
                    "Нужен provider для реального получения значения."),
            };

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

        private static DeviceStatusSnapshot ProbeComEnvironment(SerialPortProbeReport report)
        {
            if (report.ProbeError is not null)
            {
                return new DeviceStatusSnapshot
                {
                    Id = "com",
                    DisplayName = "COM / последовательные порты",
                    State = DeviceReadinessState.Error,
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
                    Detail = "COM-порты не обнаружены.",
                    IsBlocking = false
                };
            }

            if (report.AccessiblePorts.Length == 0)
            {
                return new DeviceStatusSnapshot
                {
                    Id = "com",
                    DisplayName = "COM / последовательные порты",
                    State = DeviceReadinessState.Unavailable,
                    Detail = $"Порты найдены, но не открываются: {string.Join("; ", report.InaccessiblePorts)}",
                    IsBlocking = false
                };
            }

            var detail = $"Порты открываются: {string.Join(", ", report.AccessiblePorts)}. Реальное устройство не подтверждено, рукопожатие не выполнялось.";
            if (report.InaccessiblePorts.Length > 0)
            {
                detail += $" Недоступны: {string.Join("; ", report.InaccessiblePorts)}.";
            }

            return new DeviceStatusSnapshot
            {
                Id = "com",
                DisplayName = "COM / последовательные порты",
                State = DeviceReadinessState.Warning,
                Detail = detail,
                IsBlocking = false
            };
        }

        private static DeviceStatusSnapshot BuildPeripheralStatus(
            string id,
            string displayName,
            bool diagnosticsMode,
            SerialPortProbeReport report,
            string integrationNote)
        {
            if (report.ProbeError is not null)
            {
                return new DeviceStatusSnapshot
                {
                    Id = id,
                    DisplayName = displayName,
                    State = DeviceReadinessState.Error,
                    Detail = $"COM-окружение недоступно: {report.ProbeError}. {integrationNote}",
                    IsBlocking = !diagnosticsMode
                };
            }

            var portFact = BuildPortFact(report);
            if (diagnosticsMode)
            {
                return new DeviceStatusSnapshot
                {
                    Id = id,
                    DisplayName = displayName,
                    State = DeviceReadinessState.Diagnostics,
                    Detail = $"{portFact} Реальная готовность устройства не подтверждена. Доступен только diagnostics fallback. {integrationNote}",
                    IsBlocking = false
                };
            }

            if (report.AccessiblePorts.Length == 0)
            {
                return new DeviceStatusSnapshot
                {
                    Id = id,
                    DisplayName = displayName,
                    State = report.DetectedPorts.Length == 0
                        ? DeviceReadinessState.Missing
                        : DeviceReadinessState.Unavailable,
                    Detail = $"{portFact} Реальный модуль не подтвержден и не готов к запуску. {integrationNote}",
                    IsBlocking = true
                };
            }

            return new DeviceStatusSnapshot
            {
                Id = id,
                DisplayName = displayName,
                State = DeviceReadinessState.Warning,
                Detail = $"{portFact} Порт есть, но устройство не подтверждено: нет рукопожатия или device provider. {integrationNote}",
                IsBlocking = true
            };
        }

        private static SerialPortProbeReport ProbeSerialPorts()
        {
            var ports = GetSerialPorts(out var error);
            if (error is not null)
            {
                return new SerialPortProbeReport(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), error);
            }

            var accessible = new List<string>();
            var inaccessible = new List<string>();

            foreach (var portName in ports)
            {
                var openError = TryOpenPort(portName);
                if (openError is null)
                {
                    accessible.Add(portName);
                }
                else
                {
                    inaccessible.Add($"{portName} ({openError})");
                }
            }

            return new SerialPortProbeReport(ports, accessible.ToArray(), inaccessible.ToArray(), null);
        }

        private static string BuildPortFact(SerialPortProbeReport report)
        {
            if (report.ProbeError is not null)
            {
                return "COM-окружение недоступно.";
            }

            if (report.DetectedPorts.Length == 0)
            {
                return "COM-порты не обнаружены.";
            }

            var parts = new List<string>();
            if (report.AccessiblePorts.Length > 0)
            {
                parts.Add($"Доступны: {string.Join(", ", report.AccessiblePorts)}");
            }

            if (report.InaccessiblePorts.Length > 0)
            {
                parts.Add($"Недоступны: {string.Join("; ", report.InaccessiblePorts)}");
            }

            return parts.Count == 0
                ? $"Обнаружены порты: {string.Join(", ", report.DetectedPorts)}."
                : string.Join(". ", parts) + ".";
        }

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

        private sealed record SerialPortProbeReport(
            string[] DetectedPorts,
            string[] AccessiblePorts,
            string[] InaccessiblePorts,
            string? ProbeError);
    }
}
