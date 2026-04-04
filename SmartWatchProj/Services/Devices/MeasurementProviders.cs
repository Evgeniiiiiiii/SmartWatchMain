using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SmartWatchProj.Models;
using SmartWatchProj.Services.Diagnostics;

namespace SmartWatchProj.Services.Devices
{
    public interface IMeasurementProvider
    {
        string ProviderName { get; }
        Task<VitalMeasurement> CaptureAsync(int employeeId, CancellationToken cancellationToken = default);
    }

    public interface IEmergencyStoppableMeasurementProvider
    {
        Task EmergencyStopAsync(string reason, CancellationToken cancellationToken = default);
    }

    public enum MeasurementWorkflowStage
    {
        PrepareTemperature,
        MeasureTemperature,
        PrepareAlcohol,
        MeasureAlcohol,
        PreparePressure,
        MeasurePressure,
        ProcessingResults
    }

    public sealed class MeasurementWorkflowHooks
    {
        public Func<MeasurementWorkflowStage, CancellationToken, Task>? OnStageAsync { get; init; }
    }

    public sealed class DiagnosticsMeasurementProvider : IMeasurementProvider
    {
        private readonly RuntimeLogStore logStore;
        private int runCounter;

        public DiagnosticsMeasurementProvider(RuntimeLogStore logStore)
        {
            this.logStore = logStore;
        }

        public string ProviderName => "Diagnostics Stub";

        public Task<VitalMeasurement> CaptureAsync(int employeeId, CancellationToken cancellationToken = default)
        {
            var template = templates[runCounter % templates.Length];
            runCounter++;

            logStore.Info("Measurements", $"Diagnostics profile selected: {template.Name}.");

            return Task.FromResult(new VitalMeasurement
            {
                EmployeeId = employeeId,
                Timestamp = DateTime.Now,
                HeartRate = template.HeartRate,
                Saturation = template.Saturation,
                EcgData = $"diagnostics:{template.Name}",
                ActivityLevel = template.ActivityLevel,
                BloodPressureSystolic = template.BloodPressureSystolic,
                BloodPressureDiastolic = template.BloodPressureDiastolic,
                Temperature = template.Temperature,
                Glucose = template.Glucose,
                Cholesterol = template.Cholesterol,
                AlcoholLevel = template.AlcoholLevel,
                Diagnosis = $"Diagnostics profile: {template.Name}"
            });
        }

        private static readonly MeasurementTemplate[] templates =
        {
            new("Норма", 76, 98, 122, 78, 36.6, 5.1, 4.8, 0.0, 2800),
            new("Внимание", 104, 94, 142, 92, 37.3, 6.4, 5.9, 0.1, 1600),
            new("Риск", 128, 89, 168, 104, 37.9, 7.8, 6.6, 0.5, 900)
        };

        private sealed record MeasurementTemplate(
            string Name,
            double HeartRate,
            double Saturation,
            double BloodPressureSystolic,
            double BloodPressureDiastolic,
            double Temperature,
            double Glucose,
            double Cholesterol,
            double AlcoholLevel,
            double ActivityLevel);
    }

    public sealed class PendingHardwareMeasurementProvider : IMeasurementProvider
    {
        public string ProviderName => "Hardware Bridge Pending";

        public Task<VitalMeasurement> CaptureAsync(int employeeId, CancellationToken cancellationToken = default)
        {
            return Task.FromException<VitalMeasurement>(
                new InvalidOperationException(
                    "Реальный сбор данных еще не подключен. Для production-режима нужно использовать настроенный hardware provider."));
        }
    }

    public sealed class SerialHardwareMeasurementProvider : IMeasurementProvider, IEmergencyStoppableMeasurementProvider
    {
        private const string ConfigFileName = "device-port-map.json";
        private const string TemperatureCommand = "x1x1x";
        private const string AlcoholCommand = "x1x2x";
        private const string PressureCommand = "x1x3x";
        private static readonly Regex TemperatureRegex = new(@"IRTemperature\s*=\s*(?<value>-?\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex AlcoholRegex = new(@"Alco\s+value\s*=\s*(?<value>-?\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex SadRegex = new(@"NIBP\s+SAD\s*=\s*(?<value>-?\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex DadRegex = new(@"NIBP\s+DAD\s*=\s*(?<value>-?\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex PressureCompleteRegex = new(@"NIBP\s+measure\s+is\s+complete", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex FinalJsonRegex = new(@"\{[^\{\}]*?(Temp|\""Temp\"")\s*:\s*[-\d.,]+[^\{\}]*?(Alco|\""Alco\"")\s*:\s*[-\d.,]+[^\{\}]*?(SYS|\""SYS\"")\s*:\s*[-\d.,]+[^\{\}]*?(DAD|\""DAD\"")\s*:\s*[-\d.,]+[^\{\}]*\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

        private readonly string baseDirectory;
        private readonly RuntimeLogStore logStore;
        private readonly object activePortSync = new();
        private SerialPort? activePort;
        private Esp32ControllerConfig? activeControllerConfig;
        private string? activePortName;

        public SerialHardwareMeasurementProvider(string baseDirectory, RuntimeLogStore logStore)
        {
            this.baseDirectory = baseDirectory;
            this.logStore = logStore;
        }

        public string ProviderName => "ESP32 Serial Controller";

        public async Task<VitalMeasurement> CaptureAsync(int employeeId, CancellationToken cancellationToken = default)
            => await CaptureAsync(employeeId, hooks: null, cancellationToken);

        public async Task<VitalMeasurement> CaptureAsync(
            int employeeId,
            MeasurementWorkflowHooks? hooks,
            CancellationToken cancellationToken = default)
        {
            var config = LoadConfig();
            var controller = RequireController(config.Controller);
            var controllerPort = controller.ResolveConfiguredPort() ?? controller.EnumerateCandidatePorts().First();

            logStore.Info("Measurements", $"ESP32 controller measurement started on {controllerPort}.");
            var controllerResponse = await ReadControllerAsync(controller, controllerPort, hooks, cancellationToken);

            var measurement = new VitalMeasurement
            {
                EmployeeId = employeeId,
                Timestamp = DateTime.Now,
                Temperature = controllerResponse.Temp,
                AlcoholLevel = controllerResponse.Alco,
                BloodPressureSystolic = controllerResponse.Sys,
                BloodPressureDiastolic = controllerResponse.Dad,
                Diagnosis = controllerResponse.ResultSummary
            };

            logStore.Info("Measurements", $"ESP32 controller measurement complete for employee {employeeId}. Source={controllerResponse.SourceLabel}.");
            return measurement;
        }

        private DevicePortMapConfig LoadConfig()
        {
            var configPath = Path.Combine(baseDirectory, ConfigFileName);
            if (!File.Exists(configPath))
            {
                throw new FileNotFoundException(
                    $"Обязательный файл конфигурации {ConfigFileName} не найден. Production-режим без него запрещен.",
                    configPath);
            }

            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<DevicePortMapConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return config ?? throw new InvalidOperationException($"Не удалось прочитать {ConfigFileName}.");
        }

        private async Task<ControllerResponse> ReadControllerAsync(
            Esp32ControllerConfig controller,
            string configuredPort,
            MeasurementWorkflowHooks? hooks,
            CancellationToken cancellationToken)
        {
            Exception? lastException = null;
            foreach (var candidatePort in controller.EnumerateCandidatePorts())
            {
                var serialNewLine = ResolveSerialNewLine(controller.NewLine);
                using var port = new SerialPort(candidatePort, controller.BaudRate, ParseParity(controller.Parity), controller.DataBits, ParseStopBits(controller.StopBits))
                {
                    ReadTimeout = controller.SerialOperationTimeoutMs,
                    WriteTimeout = controller.SerialOperationTimeoutMs,
                    NewLine = serialNewLine,
                    DtrEnable = controller.DtrEnable,
                    RtsEnable = controller.RtsEnable
                };

                var portOpened = false;
                try
                {
                    logStore.Info("COM", $"SerialPort initialized: Port={candidatePort}; Baud={controller.BaudRate}; NewLine={FormatControlCharacters(serialNewLine)}");
                    logStore.Info("COM", $"Open controller port: {candidatePort}");
                    port.Open();
                    portOpened = true;
                    logStore.Info("COM", $"Open success: {candidatePort}");
                    await DrainBootBannerAsync(port, controller, cancellationToken);
                    port.DiscardInBuffer();
                    port.DiscardOutBuffer();
                    RegisterActivePort(port, controller, candidatePort);

                    var response = await RunMeasurementScenarioAsync(port, controller, hooks, cancellationToken);
                    logStore.Info("COM", "Measurement scenario complete");
                    return response;
                }
                catch (TimeoutException ex)
                {
                    lastException = ex;
                    logStore.Error("COM", $"Runtime step timeout: {ex.Message}");
                    await EmergencyStopInternalAsync(port, controller, $"timeout on {candidatePort}", cancellationToken);
                    logStore.Error("COM", "Measurement aborted for safety");
                    throw new InvalidOperationException($"Timeout / аварийная остановка: {ex.Message}", ex);
                }
                catch (OperationCanceledException ex)
                {
                    lastException = ex;
                    await EmergencyStopInternalAsync(port, controller, $"cancelled on {candidatePort}", CancellationToken.None);
                    logStore.Warning("COM", "Measurement aborted for safety");
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastException = ex;
                    if (portOpened)
                    {
                        await EmergencyStopInternalAsync(port, controller, $"failure on {candidatePort}", cancellationToken);
                        logStore.Error("COM", "Measurement aborted for safety");
                        throw new InvalidOperationException($"Измерение прервано / аварийная остановка: {ex.Message}", ex);
                    }

                    logStore.Error("COM", $"Open failed: {candidatePort} ({ex.Message})");
                }
                finally
                {
                    ClearActivePort(port);
                }
            }

            throw new InvalidOperationException(
                $"Failed to open controller port. Configured path: {configuredPort}. Last error: {lastException?.Message ?? "unknown"}",
                lastException);
        }

        private async Task SendControllerCommandAsync(
            SerialPort port,
            string command,
            int waitMs,
            CancellationToken cancellationToken)
        {
            logStore.Info("COM", $"Send command: {command}");
            port.Write(command);
            await Task.Delay(Math.Min(waitMs, 150), cancellationToken);
        }

        private async Task<ControllerResponse> RunMeasurementScenarioAsync(
            SerialPort port,
            Esp32ControllerConfig controller,
            MeasurementWorkflowHooks? hooks,
            CancellationToken cancellationToken)
        {
            await ReportStageAsync(hooks, MeasurementWorkflowStage.PrepareTemperature, cancellationToken);
            await ReportStageAsync(hooks, MeasurementWorkflowStage.MeasureTemperature, cancellationToken);
            await SendControllerCommandAsync(port, TemperatureCommand, controller.DelayBetweenCommandsMs, cancellationToken);
            var temperaturePayload = await ReadUntilMatchAsync(port, controller.TemperatureTimeoutMs, cancellationToken, "IRTemperature", TemperatureRegex);
            var temperature = ParseMatchedDouble(temperaturePayload, TemperatureRegex, "temperature");
            logStore.Info("COM", $"Parsed temperature: {temperature}");

            await ReportStageAsync(hooks, MeasurementWorkflowStage.PrepareAlcohol, cancellationToken);
            await ReportStageAsync(hooks, MeasurementWorkflowStage.MeasureAlcohol, cancellationToken);
            await SendControllerCommandAsync(port, AlcoholCommand, controller.DelayBetweenCommandsMs, cancellationToken);
            var alcoholPayload = await ReadUntilMatchAsync(port, controller.AlcoholTimeoutMs, cancellationToken, "Alco value", AlcoholRegex);
            var alcohol = ParseMatchedDouble(alcoholPayload, AlcoholRegex, "alco");
            logStore.Info("COM", $"Parsed alco: {alcohol}");

            await ReportStageAsync(hooks, MeasurementWorkflowStage.PreparePressure, cancellationToken);
            await ReportStageAsync(hooks, MeasurementWorkflowStage.MeasurePressure, cancellationToken);
            await SendControllerCommandAsync(port, PressureCommand, controller.DelayBetweenCommandsMs, cancellationToken);
            var pressurePayload = await ReadUntilAllMatchesAsync(
                port,
                controller.PressureTimeoutMs,
                cancellationToken,
                "NIBP completion + SAD + DAD",
                PressureCompleteRegex,
                SadRegex,
                DadRegex);

            var sad = ParseMatchedDouble(pressurePayload, SadRegex, "SAD");
            var dad = ParseMatchedDouble(pressurePayload, DadRegex, "DAD");
            logStore.Info("COM", $"Parsed SAD: {sad}");
            logStore.Info("COM", $"Parsed DAD: {dad}");

            await ReportStageAsync(hooks, MeasurementWorkflowStage.ProcessingResults, cancellationToken);
            var finalJsonPayload = await TryReadFinalJsonAsync(port, controller.FinalJsonTimeoutMs, cancellationToken);
            if (!string.IsNullOrWhiteSpace(finalJsonPayload))
            {
                logStore.Info("COM", $"Final raw payload: {finalJsonPayload}");
                var finalJson = ParseFinalJsonPayload(finalJsonPayload);
                logStore.Info("COM", "JSON parse success");
                return new ControllerResponse(
                    finalJson.Temp,
                    finalJson.Alco,
                    finalJson.Sys,
                    finalJson.Dad,
                    "final-json",
                    BuildResultSummary(finalJson.Temp, finalJson.Alco, finalJson.Sys, finalJson.Dad, fromFinalJson: true));
            }

            logStore.Warning("COM", "JSON parse failed: final controller JSON not received, falling back to debug/raw readings.");
            return new ControllerResponse(
                temperature,
                alcohol,
                sad,
                dad,
                "debug-raw",
                BuildResultSummary(temperature, alcohol, sad, dad, fromFinalJson: false));
        }

        private static Task ReportStageAsync(
            MeasurementWorkflowHooks? hooks,
            MeasurementWorkflowStage stage,
            CancellationToken cancellationToken) =>
            hooks?.OnStageAsync is null
                ? Task.CompletedTask
                : hooks.OnStageAsync(stage, cancellationToken);

        public Task EmergencyStopAsync(string reason, CancellationToken cancellationToken = default)
        {
            SerialPort? port;
            Esp32ControllerConfig? controller;

            lock (activePortSync)
            {
                port = activePort;
                controller = activeControllerConfig;
            }

            if (port is null || controller is null)
            {
                logStore.Warning("COM", $"Emergency stop requested: {reason}. No active port.");
                return Task.CompletedTask;
            }

            return EmergencyStopInternalAsync(port, controller, reason, cancellationToken);
        }

        private async Task EmergencyStopInternalAsync(
            SerialPort port,
            Esp32ControllerConfig controller,
            string reason,
            CancellationToken cancellationToken)
        {
            logStore.Warning("COM", $"Emergency stop requested: {reason}");
            logStore.Warning("COM", "Controller reset requested");

            try
            {
                if (port.IsOpen && !string.IsNullOrWhiteSpace(controller.ResetCommand))
                {
                    port.Write(controller.ResetCommand);
                    await Task.Delay(controller.ResetCommandDelayMs, cancellationToken);
                    logStore.Warning("COM", $"Reset command sent: {controller.ResetCommand}");
                }
            }
            catch (Exception ex)
            {
                logStore.Error("COM", $"Controller reset failed: {ex.Message}");
            }

            try
            {
                if (port.IsOpen)
                {
                    port.DiscardInBuffer();
                    port.DiscardOutBuffer();
                    port.Close();
                    logStore.Warning("COM", "Port closed");
                }
            }
            catch (Exception ex)
            {
                logStore.Error("COM", $"Port close failed: {ex.Message}");
            }
        }

        private void RegisterActivePort(SerialPort port, Esp32ControllerConfig controller, string portName)
        {
            lock (activePortSync)
            {
                activePort = port;
                activeControllerConfig = controller;
                activePortName = portName;
            }
        }

        private void ClearActivePort(SerialPort port)
        {
            lock (activePortSync)
            {
                if (ReferenceEquals(activePort, port))
                {
                    activePort = null;
                    activeControllerConfig = null;
                    activePortName = null;
                }
            }
        }

        private async Task<string> ReadUntilMatchAsync(
            SerialPort port,
            int timeoutMs,
            CancellationToken cancellationToken,
            string expectedLabel,
            Regex pattern)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var buffer = string.Empty;

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunk = port.ReadExisting();
                if (!string.IsNullOrEmpty(chunk))
                {
                    logStore.Info("COM", $"Raw chunk: {chunk}");
                    buffer += chunk;
                    if (pattern.IsMatch(buffer))
                    {
                        return buffer;
                    }
                }

                await Task.Delay(100, cancellationToken);
            }

            throw new TimeoutException($"ESP32 controller did not return {expectedLabel} within {timeoutMs} ms. Payload={buffer}");
        }

        private async Task<string> ReadUntilAllMatchesAsync(
            SerialPort port,
            int timeoutMs,
            CancellationToken cancellationToken,
            string expectedLabel,
            params Regex[] patterns)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var buffer = string.Empty;

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunk = port.ReadExisting();
                if (!string.IsNullOrEmpty(chunk))
                {
                    logStore.Info("COM", $"Raw chunk: {chunk}");
                    buffer += chunk;
                    if (patterns.All(pattern => pattern.IsMatch(buffer)))
                    {
                        return buffer;
                    }
                }

                await Task.Delay(100, cancellationToken);
            }

            throw new TimeoutException($"ESP32 controller did not return {expectedLabel} within {timeoutMs} ms. Payload={buffer}");
        }

        private async Task<string?> TryReadFinalJsonAsync(
            SerialPort port,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var buffer = string.Empty;

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunk = port.ReadExisting();
                if (!string.IsNullOrEmpty(chunk))
                {
                    logStore.Info("COM", $"Raw chunk: {chunk}");
                    buffer += chunk;
                    var jsonMatch = FinalJsonRegex.Match(buffer);
                    if (jsonMatch.Success)
                    {
                        return jsonMatch.Value;
                    }
                }

                await Task.Delay(100, cancellationToken);
            }

            return null;
        }

        private FinalJsonPayload ParseFinalJsonPayload(string payload)
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                var root = document.RootElement;
                return new FinalJsonPayload(
                    GetRequiredJsonDouble(root, "Temp"),
                    GetRequiredJsonDouble(root, "Alco"),
                    GetRequiredJsonDouble(root, "SYS"),
                    GetRequiredJsonDouble(root, "DAD"));
            }
            catch (Exception ex)
            {
                logStore.Warning("COM", $"JSON parse failed: {ex.Message}");
                throw;
            }
        }

        private static double GetRequiredJsonDouble(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var property))
            {
                throw new InvalidOperationException($"Controller JSON does not contain {propertyName}.");
            }

            return property.ValueKind switch
            {
                JsonValueKind.Number => property.GetDouble(),
                JsonValueKind.String when double.TryParse(property.GetString()?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) => value,
                _ => throw new InvalidOperationException($"Controller JSON field {propertyName} is not numeric.")
            };
        }

        private static string BuildResultSummary(double temperature, double alcohol, double sys, double dad, bool fromFinalJson)
        {
            var source = fromFinalJson ? "final JSON" : "debug/raw";
            var issues = new List<string>();

            if (alcohol >= 4095)
            {
                issues.Add("алкоголь выглядит как raw ADC 4095");
            }

            if (sys == 255 || dad == 255)
            {
                issues.Add("давление 255/255 выглядит как invalid/raw result from firmware");
            }

            return issues.Count == 0
                ? $"ESP32 measurement received from {source}"
                : $"ESP32 measurement received from {source}; invalid/raw result from firmware: {string.Join("; ", issues)}";
        }

        private async Task DrainBootBannerAsync(SerialPort port, Esp32ControllerConfig controller, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(controller.BootBannerDrainTimeoutMs);
            var buffer = string.Empty;

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = port.ReadExisting();
                if (!string.IsNullOrEmpty(chunk))
                {
                    buffer += chunk;
                }

                await Task.Delay(50, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(buffer))
            {
                logStore.Warning("COM", $"Controller startup banner detected after open: {buffer}");
            }
        }

        private static double ParseMatchedDouble(string payload, Regex regex, string fieldName)
        {
            var match = regex.Match(payload);
            if (!match.Success)
            {
                throw new InvalidOperationException($"ESP32 payload does not contain {fieldName}. Payload={payload}");
            }

            var text = match.Groups["value"].Value.Replace(',', '.');
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                throw new InvalidOperationException($"ESP32 field {fieldName} is not a valid number: {text}");
            }

            return value;
        }

        private static Esp32ControllerConfig RequireController(Esp32ControllerConfig? controller) =>
            controller ?? throw new InvalidOperationException(
                $"В {ConfigFileName} не настроен блок 'controller'. Для production-режима нужен один реальный COM-порт ESP32.");

        private static Parity ParseParity(string? value) =>
            Enum.TryParse<Parity>(value, ignoreCase: true, out var parsed) ? parsed : Parity.None;

        private static StopBits ParseStopBits(string? value) =>
            Enum.TryParse<StopBits>(value, ignoreCase: true, out var parsed) ? parsed : StopBits.One;

        private static string ResolveSerialNewLine(string? configuredNewLine) =>
            string.IsNullOrWhiteSpace(configuredNewLine)
                ? "\n"
                : configuredNewLine;

        private static string FormatControlCharacters(string value) =>
            "\"" + value
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

        private sealed record ControllerResponse(double Temp, double Alco, double Sys, double Dad, string SourceLabel, string ResultSummary);
        private sealed record FinalJsonPayload(double Temp, double Alco, double Sys, double Dad);

        private sealed class DevicePortMapConfig
        {
            [JsonPropertyName("controller")]
            public Esp32ControllerConfig? Controller { get; init; }
        }

        private sealed class Esp32ControllerConfig
        {
            public string? Port { get; init; }

            [JsonPropertyName("portCandidates")]
            public string[] PortCandidates { get; init; } = Array.Empty<string>();

            [JsonPropertyName("newLine")]
            public string? NewLine { get; init; }

            public int BaudRate { get; init; } = 115200;
            public string? Parity { get; init; } = "None";
            public int DataBits { get; init; } = 8;
            public string? StopBits { get; init; } = "One";
            public int DelayBetweenCommandsMs { get; init; } = 6000;
            public int SerialOperationTimeoutMs { get; init; } = 1000;
            public int TemperatureTimeoutMs { get; init; } = 4000;
            public int AlcoholTimeoutMs { get; init; } = 4000;
            public int PressureTimeoutMs { get; init; } = 25000;
            public int FinalJsonTimeoutMs { get; init; } = 5000;
            public int BootBannerDrainTimeoutMs { get; init; } = 1200;
            public int ResetCommandDelayMs { get; init; } = 150;
            public bool DtrEnable { get; init; }
            public bool RtsEnable { get; init; }
            public string? ResetCommand { get; init; }

            public string? ResolveConfiguredPort() =>
                EnumerateCandidatePorts().FirstOrDefault();

            public IEnumerable<string> EnumerateCandidatePorts()
            {
                var candidates = new List<string>();
                if (!string.IsNullOrWhiteSpace(Port))
                {
                    candidates.Add(Port.Trim());
                }

                candidates.AddRange(PortCandidates
                    .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                    .Select(candidate => candidate.Trim()));

                if (candidates.Count == 0)
                {
                    throw new InvalidOperationException($"В {ConfigFileName} не указан порт ESP32 controller.");
                }

                IEnumerable<string> orderedCandidates = candidates.Distinct(StringComparer.Ordinal);
                if (OperatingSystem.IsLinux())
                {
                    orderedCandidates = orderedCandidates
                        .OrderBy(GetLinuxPriority)
                        .ThenBy(candidate => candidate, StringComparer.Ordinal);
                }

                foreach (var candidate in orderedCandidates)
                {
                    yield return candidate;
                }
            }

            private static int GetLinuxPriority(string candidate)
            {
                if (string.Equals(candidate, "/dev/ttyUSB0", StringComparison.OrdinalIgnoreCase))
                {
                    return 0;
                }

                if (candidate.StartsWith("/dev/ttyUSB", StringComparison.OrdinalIgnoreCase))
                {
                    return 1;
                }

                if (candidate.StartsWith("/dev/ttyACM", StringComparison.OrdinalIgnoreCase))
                {
                    return 2;
                }

                if (candidate.Contains("/dev/serial/by-id/", StringComparison.OrdinalIgnoreCase))
                {
                    return 3;
                }

                return 4;
            }
        }
    }
}
