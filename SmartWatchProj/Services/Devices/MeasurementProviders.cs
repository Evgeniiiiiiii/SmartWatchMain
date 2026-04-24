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
                HasAlcoholValue = true,
                HasTemperatureValue = true,
                HasPressureValue = true,
                AlcoholAssessmentSource = "debug-value",
                TemperatureAssessmentSource = "own-step",
                PressureAssessmentSource = "own-step",
                Diagnosis = $"Diagnostics profile: {template.Name}"
            });
        }

        private static readonly MeasurementTemplate[] templates =
        {
            new("Норма", 76, 98, 122, 78, 36.6, 5.1, 4.8, 3500, 2800),
            new("Внимание", 104, 94, 142, 92, 37.3, 6.4, 5.9, 1600, 1600),
            new("Риск", 128, 89, 168, 104, 37.9, 7.8, 6.6, 900, 900)
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
        private const double MinimumHumanTemperature = 30.0;
        private const double InvalidAlcoholRawThreshold = 4095.0;
        private static readonly Regex[] TemperatureRegexes =
        {
            new(@"IRTemperature\s*[:=]\s*(?<value>-?\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new(@"\b(?:IR\s*)?Temp(?:erature)?\b\s*[:=]\s*(?<value>-?\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        };
        private static readonly Regex[] AlcoholRegexes =
        {
            new(@"Alco\s+value\s*[:=]\s*(?<value>-?\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new(@"\bAlco(?:hol)?(?:\s+value)?\b\s*[:=]\s*(?<value>-?\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        };
        private static readonly Regex[] SadRegexes =
        {
            new(@"NIBP\s+(?:SAD|SYS)\s*[:=]\s*(?<value>-?\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new(@"\b(?:SAD|SYS)\b\s*[:=]\s*(?<value>-?\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        };
        private static readonly Regex[] DadRegexes =
        {
            new(@"NIBP\s+(?:DAD|DIA)\s*[:=]\s*(?<value>-?\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new(@"\b(?:DAD|DIA)\b\s*[:=]\s*(?<value>-?\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        };
        private static readonly Regex[] PressureCompleteRegexes =
        {
            new(@"NIBP\s+measure\s+is\s+complete", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new(@"\bNIBP\b.*\bcomplete\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new(@"\bpressure\b.*\bcomplete\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        };
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
                HasAlcoholValue = controllerResponse.HasAlcoholValue,
                HasTemperatureValue = controllerResponse.HasTemperatureValue,
                HasPressureValue = controllerResponse.HasPressureValue,
                AlcoholAssessmentSource = controllerResponse.AlcoholSource,
                TemperatureAssessmentSource = controllerResponse.TemperatureSource,
                PressureAssessmentSource = controllerResponse.PressureSource,
                BloodPressureSystolic = controllerResponse.Sys,
                BloodPressureDiastolic = controllerResponse.Dad,
                Diagnosis = controllerResponse.ResultSummary
            };

            logStore.Info("Measurements", $"temperature preserved into final measurement: {measurement.Temperature.ToString("F2", CultureInfo.InvariantCulture)}");
            logStore.Info("Measurements", $"alcohol preserved into final measurement: {measurement.AlcoholLevel.ToString("F0", CultureInfo.InvariantCulture)}");
            logStore.Info("Measurements", $"pressure preserved into final measurement: SYS={measurement.BloodPressureSystolic.ToString("F0", CultureInfo.InvariantCulture)}, DAD={measurement.BloodPressureDiastolic.ToString("F0", CultureInfo.InvariantCulture)}");
            logStore.Info("Measurements", $"final measurement source summary: temp={measurement.TemperatureAssessmentSource}, alco={measurement.AlcoholAssessmentSource}, pressure={measurement.PressureAssessmentSource}");
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
            logStore.Info("COM", "temperature measurement command sent");
            await SendControllerCommandAsync(port, TemperatureCommand, controller.DelayBetweenCommandsMs, cancellationToken);
            var temperatureRead = await ReadMeasurementValueAsync(
                port,
                controller.TemperatureTimeoutMs,
                cancellationToken,
                "temperature",
                TryRecognizeTemperatureCandidate);
            var temperature = temperatureRead.Value;
            var temperatureSource = temperatureRead.Source;
            if (temperature is <= MinimumHumanTemperature)
            {
                logStore.Warning("COM", $"Temperature looks not ready yet: {temperature}. Waiting {controller.TemperatureRetryDelayMs} ms and retrying x1x1x once.");
                await WaitForMeasurementWindowAsync("temperature retry", controller.TemperatureRetryDelayMs, cancellationToken);
                await SendControllerCommandAsync(port, TemperatureCommand, controller.DelayBetweenCommandsMs, cancellationToken);
                var temperatureRetryRead = await ReadMeasurementValueAsync(
                    port,
                    controller.TemperatureTimeoutMs,
                    cancellationToken,
                    "temperature",
                    TryRecognizeTemperatureCandidate);
                if (temperatureRetryRead.Value.HasValue)
                {
                    temperature = temperatureRetryRead.Value;
                    temperatureSource = temperatureRetryRead.Source;
                }
            }

            double? confirmedTemperature = null;
            var confirmedTemperatureSource = "missing";
            if (HasReceivedTemperatureValue(temperature))
            {
                confirmedTemperature = temperature.GetValueOrDefault();
                confirmedTemperatureSource = string.IsNullOrWhiteSpace(temperatureSource) ? "own-step" : temperatureSource;
                logStore.Info("COM", $"Parsed temperature: {confirmedTemperature}");
                logStore.Info("COM", $"temperature candidate recognized from own-step payload: {confirmedTemperature.Value.ToString("F2", CultureInfo.InvariantCulture)}");
                logStore.Info("COM", $"temperature accepted with source={confirmedTemperatureSource}");
            }
            else
            {
                logStore.Warning("COM", "temperature debug missing, awaiting final JSON");
            }

            await ReportStageAsync(hooks, MeasurementWorkflowStage.PrepareAlcohol, cancellationToken);
            await ReportStageAsync(hooks, MeasurementWorkflowStage.MeasureAlcohol, cancellationToken);
            logStore.Info("COM", "alcohol measurement command sent");
            await SendControllerCommandAsync(port, AlcoholCommand, controller.DelayBetweenCommandsMs, cancellationToken);
            var alcoholRead = await ReadMeasurementValueAsync(
                port,
                controller.AlcoholTimeoutMs,
                cancellationToken,
                "alcohol",
                TryRecognizeAlcoholCandidate);
            var alcohol = alcoholRead.Value;
            var alcoholSource = alcoholRead.Source;
            if (!alcohol.HasValue)
            {
                logStore.Warning("COM", $"Alcohol value not received yet. Waiting {controller.AlcoholRetryDelayMs} ms and retrying x1x2x once.");
                await WaitForMeasurementWindowAsync("alcohol retry", controller.AlcoholRetryDelayMs, cancellationToken);
                await SendControllerCommandAsync(port, AlcoholCommand, controller.DelayBetweenCommandsMs, cancellationToken);
                var alcoholRetryRead = await ReadMeasurementValueAsync(
                    port,
                    controller.AlcoholTimeoutMs,
                    cancellationToken,
                    "alcohol",
                    TryRecognizeAlcoholCandidate);
                if (alcoholRetryRead.Value.HasValue)
                {
                    alcohol = alcoholRetryRead.Value;
                    alcoholSource = alcoholRetryRead.Source;
                }
            }

            double? confirmedAlcohol = null;
            var confirmedAlcoholSource = "missing";
            if (HasReceivedAlcoholValue(alcohol))
            {
                confirmedAlcohol = alcohol.GetValueOrDefault();
                confirmedAlcoholSource = string.IsNullOrWhiteSpace(alcoholSource) ? "own-step" : alcoholSource;
                logStore.Info("COM", $"Parsed alco: {confirmedAlcohol}");
                logStore.Info("COM", $"alcohol candidate recognized from own-step payload: {confirmedAlcohol.Value.ToString("F0", CultureInfo.InvariantCulture)}");
                logStore.Info("COM", $"alcohol accepted with source={confirmedAlcoholSource}");
            }
            else
            {
                logStore.Warning("COM", "alcohol debug missing, awaiting final JSON");
            }

            await ReportStageAsync(hooks, MeasurementWorkflowStage.PreparePressure, cancellationToken);
            await ReportStageAsync(hooks, MeasurementWorkflowStage.MeasurePressure, cancellationToken);
            logStore.Info("COM", "Pressure measurement dispatch starting after prep confirmation.");
            ResetPressureMeasurementState(port);
            logStore.Info("COM", $"Pressure command dispatch starting: {PressureCommand}");
            logStore.Info("COM", "pressure measurement command sent");
            await SendControllerCommandAsync(port, PressureCommand, controller.DelayBetweenCommandsMs, cancellationToken);
            var pressureReadResult = await ReadPressureAndTryFinalJsonAsync(
                port,
                controller.PressureTimeoutMs,
                controller.FinalJsonTimeoutMs,
                cancellationToken,
                "NIBP completion + SAD + DAD");
            await ReportStageAsync(hooks, MeasurementWorkflowStage.ProcessingResults, cancellationToken);
            logStore.Info("COM", "full measurement cycle completed before final validation");

            var finalJsonPayload = pressureReadResult.FinalJsonPayload;
            if (!string.IsNullOrWhiteSpace(finalJsonPayload))
            {
                logStore.Info("COM", $"Final raw payload: {finalJsonPayload}");
                var finalJson = ParseFinalJsonPayload(finalJsonPayload);
                logStore.Info("COM", "JSON parse success");
                var resolvedTemperature = ResolveTemperatureMeasurementValue(
                    confirmedTemperature,
                    confirmedTemperature.HasValue ? confirmedTemperatureSource : "missing",
                    finalJson.Temp,
                    pressureReadResult.FinalJsonSource);

                var resolvedAlcohol = ResolveAlcoholMeasurementValue(
                    confirmedAlcohol,
                    confirmedAlcohol.HasValue ? confirmedAlcoholSource : "missing",
                    finalJson.Alco,
                    pressureReadResult.FinalJsonSource);

                EnsureRealPressureValues(finalJson.Sys, finalJson.Dad, pressureReadResult.FinalJsonSource);
                logStore.Info("COM", $"pressure candidate recognized: SYS={finalJson.Sys.ToString("F0", CultureInfo.InvariantCulture)}, DAD={finalJson.Dad.ToString("F0", CultureInfo.InvariantCulture)}");
                logStore.Info("COM", $"pressure final JSON accepted with source={pressureReadResult.FinalJsonSource}");
                return new ControllerResponse(
                    resolvedTemperature.Value,
                    resolvedTemperature.Source,
                    resolvedAlcohol.Value,
                    true,
                    resolvedAlcohol.Source,
                    true,
                    finalJson.Sys,
                    finalJson.Dad,
                    true,
                    pressureReadResult.FinalJsonSource,
                    "final-json",
                    BuildResultSummary(resolvedTemperature.Value, resolvedAlcohol.Value, finalJson.Sys, finalJson.Dad, fromFinalJson: true));
            }

            if (pressureReadResult.Status == PressureStepStatus.InvalidResult)
            {
                logStore.Warning("COM", "pressure not confirmed on own step");
                logStore.Warning("COM", $"Pressure step completed-with-invalid-pressure-data. Relevant={pressureReadResult.RelevantSummary}");
                logStore.Warning("COM", "controller reset initiated after pressure failure");
                throw new InvalidOperationException($"measurement flow failure reason: pressure invalid placeholder/no real pressure result. Relevant={pressureReadResult.RelevantSummary}");
            }

            logStore.Warning("COM", "pressure not confirmed on own step");
            logStore.Warning("COM", $"Pressure step completed-without-final-pressure-data. Status={pressureReadResult.Status}; Relevant={pressureReadResult.RelevantSummary}");
            logStore.Warning("COM", "controller reset initiated after pressure failure");
            throw new InvalidOperationException($"measurement flow failure reason: pressure final JSON not received. Status={pressureReadResult.Status}; Relevant={pressureReadResult.RelevantSummary}");
        }

        private async Task<PressureAndFinalJsonReadResult> ReadPressureAndTryFinalJsonAsync(
            SerialPort port,
            int pressureTimeoutMs,
            int finalJsonTimeoutMs,
            CancellationToken cancellationToken,
            string expectedLabel)
        {
            var pressureWaitStartedAt = DateTime.UtcNow;
            var effectivePressureTimeoutMs = Math.Max(pressureTimeoutMs, 35000);
            var pressureDeadline = DateTime.UtcNow.AddMilliseconds(effectivePressureTimeoutMs);
            var hardDeadline = pressureDeadline.AddMilliseconds(Math.Max(finalJsonTimeoutMs, 0));
            var currentDeadline = pressureDeadline;
            var combinedBuffer = string.Empty;
            var pressurePayload = string.Empty;
            var relevantEntries = new List<string>();
            var pressureRelatedSeen = false;
            var invalidPlaceholderSeen = false;
            var anyChunkSeen = false;
            string? invalidPlaceholderFinalJsonPayload = null;
            string? lastParseFailureReason = null;

            logStore.Info("COM", $"pressure wait started at timestamp: {pressureWaitStartedAt:O}");
            logStore.Info("COM", $"Pressure wait window started: timeout={effectivePressureTimeoutMs} ms, finalJsonTail={finalJsonTimeoutMs} ms, expected={expectedLabel}.");

            while (DateTime.UtcNow < currentDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunk = port.ReadExisting();
                if (!string.IsNullOrEmpty(chunk))
                {
                    anyChunkSeen = true;
                    logStore.Info("COM", $"raw serial chunk received on pressure step: {chunk}");
                    combinedBuffer += chunk;
                    foreach (var line in SplitPressureChunkLines(chunk))
                    {
                        var classification = ClassifyPressureLine(line);
                        logStore.Info("COM", $"parsed pressure candidate: {line}");

                        switch (classification)
                        {
                            case PressureLineClassification.JsonCandidate:
                                pressureRelatedSeen = true;
                                relevantEntries.Add(line);
                                logStore.Info("COM", $"Line classified as json-candidate: {line}");
                                break;
                            case PressureLineClassification.Relevant:
                                pressureRelatedSeen = true;
                                relevantEntries.Add(line);
                                logStore.Info("COM", $"Line classified as relevant: {line}");
                                break;
                            default:
                                logStore.Info("COM", $"pressure line ignored: reason={GetPressureIgnoredReason(line)}; line={line}");
                                break;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(pressurePayload)
                        && HasMatch(combinedBuffer, PressureCompleteRegexes)
                        && HasMatch(combinedBuffer, SadRegexes)
                        && HasMatch(combinedBuffer, DadRegexes))
                    {
                        pressurePayload = combinedBuffer;
                    }

                    if (pressureRelatedSeen && finalJsonTimeoutMs > 0)
                    {
                        var extendedDeadline = DateTime.UtcNow.AddMilliseconds(finalJsonTimeoutMs);
                        currentDeadline = extendedDeadline < hardDeadline ? extendedDeadline : hardDeadline;
                    }

                    while (TryExtractJsonCandidate(combinedBuffer, out var jsonCandidate, out var remainingBuffer))
                    {
                        combinedBuffer = remainingBuffer;
                        pressureRelatedSeen = true;
                        relevantEntries.Add($"json:{jsonCandidate}");
                        logStore.Info("COM", $"Pressure raw JSON candidate: {jsonCandidate}");

                        try
                        {
                            var finalJson = ParseFinalJsonPayload(jsonCandidate);
                            logStore.Info("COM", $"pressure final JSON accepted: Temp={finalJson.Temp}, Alco={finalJson.Alco}, SYS={finalJson.Sys}, DAD={finalJson.Dad}");

                            if (IsInvalidPressurePlaceholder(finalJson))
                            {
                                invalidPlaceholderSeen = true;
                                invalidPlaceholderFinalJsonPayload = jsonCandidate;
                                logStore.Warning("COM", $"Pressure final JSON is invalid placeholder/debug result 255/255: {jsonCandidate}");
                                logStore.Warning("COM", "pressure final JSON rejected: reason=invalid pressure placeholder 255/255");
                                continue;
                            }

                            logStore.Info("COM", $"Pressure result accepted: SYS={finalJson.Sys}, DAD={finalJson.Dad}");
                            logStore.Info("COM", "Pressure step status: success");
                            return new PressureAndFinalJsonReadResult(
                                pressurePayload,
                                jsonCandidate,
                                "final-json",
                                PressureStepStatus.Success,
                                BuildRelevantSummary(relevantEntries),
                                invalidPlaceholderFinalJsonPayload);
                        }
                        catch (Exception ex)
                        {
                            lastParseFailureReason = ex.Message;
                            logStore.Warning("COM", $"pressure final JSON rejected: reason={ex.Message}");
                        }
                    }
                }

                await Task.Delay(100, cancellationToken);
            }

            var tailFinalJsonTimeoutMs = Math.Max(finalJsonTimeoutMs, 1500);
            if (!invalidPlaceholderSeen)
            {
                logStore.Info("COM", $"Attempting pressure final JSON tail read after main wait window: tailTimeout={tailFinalJsonTimeoutMs} ms.");
                var tailFinalJsonPayload = await TryReadFinalJsonAsync(
                    port,
                    tailFinalJsonTimeoutMs,
                    cancellationToken,
                    combinedBuffer);

                if (!string.IsNullOrWhiteSpace(tailFinalJsonPayload))
                {
                    var tailFinalJson = ParseFinalJsonPayload(tailFinalJsonPayload);
                    if (IsInvalidPressurePlaceholder(tailFinalJson))
                    {
                        invalidPlaceholderSeen = true;
                        invalidPlaceholderFinalJsonPayload = tailFinalJsonPayload;
                        logStore.Warning("COM", "pressure final JSON rejected: reason=tail read returned invalid pressure placeholder 255/255");
                    }
                    else
                    {
                        relevantEntries.Add($"tail-json:{tailFinalJsonPayload}");
                        logStore.Info("COM", $"pressure final JSON accepted: Temp={tailFinalJson.Temp}, Alco={tailFinalJson.Alco}, SYS={tailFinalJson.Sys}, DAD={tailFinalJson.Dad}");
                        logStore.Info("COM", "pressure late/tail JSON accepted after main window");
                        logStore.Info("COM", "Pressure step status: success");
                        return new PressureAndFinalJsonReadResult(
                            pressurePayload,
                            tailFinalJsonPayload,
                            "recovered-from-tail",
                            PressureStepStatus.Success,
                            BuildRelevantSummary(relevantEntries),
                            invalidPlaceholderFinalJsonPayload);
                    }
                }
            }

            if (invalidPlaceholderSeen)
            {
                logStore.Warning("COM", $"port state during pressure failure: isOpen={port.IsOpen}, bytesToRead={SafeGetBytesToRead(port)}");
                logStore.Warning("COM", $"Pressure step status: invalid-result. Relevant activity: {BuildRelevantSummary(relevantEntries)}");
                return new PressureAndFinalJsonReadResult(
                    pressurePayload,
                    null,
                    "missing",
                    PressureStepStatus.InvalidResult,
                    BuildRelevantSummary(relevantEntries),
                    invalidPlaceholderFinalJsonPayload);
            }

            if (pressureRelatedSeen)
            {
                if (!string.IsNullOrWhiteSpace(lastParseFailureReason))
                {
                    logStore.Warning("COM", $"Pressure-related messages arrived, but no valid final JSON was accepted before timeout. Last parse issue: {lastParseFailureReason}");
                }
                else
                {
                    logStore.Warning("COM", "Pressure-related messages arrived, but no valid final JSON was accepted before timeout.");
                }

                logStore.Warning("COM", $"pressure wait ended with chunks but none accepted. chunksSeen={anyChunkSeen}");
                logStore.Warning("COM", $"port state during pressure failure: isOpen={port.IsOpen}, bytesToRead={SafeGetBytesToRead(port)}");
                logStore.Warning("COM", $"Pressure step status: timeout. Relevant activity: {BuildRelevantSummary(relevantEntries)}");
                return new PressureAndFinalJsonReadResult(
                    pressurePayload,
                    null,
                    "missing",
                    PressureStepStatus.Timeout,
                    BuildRelevantSummary(relevantEntries),
                    invalidPlaceholderFinalJsonPayload);
            }

            if (!anyChunkSeen)
            {
                var latePressureTimeoutMs = Math.Min(Math.Max(finalJsonTimeoutMs * 2, 8000), 15000);
                logStore.Warning("COM", $"controller-side cycle still active / awaiting late result: lateWindow={latePressureTimeoutMs} ms");
                logStore.Info("COM", $"Attempting extended pressure late read after no-data main window: timeout={latePressureTimeoutMs} ms.");
                var lateFinalJsonPayload = await TryReadFinalJsonAsync(
                    port,
                    latePressureTimeoutMs,
                    cancellationToken,
                    combinedBuffer);

                if (!string.IsNullOrWhiteSpace(lateFinalJsonPayload))
                {
                    var lateFinalJson = ParseFinalJsonPayload(lateFinalJsonPayload);
                    if (IsInvalidPressurePlaceholder(lateFinalJson))
                    {
                        invalidPlaceholderSeen = true;
                        invalidPlaceholderFinalJsonPayload = lateFinalJsonPayload;
                        logStore.Warning("COM", "pressure final JSON rejected: reason=extended late read returned invalid pressure placeholder 255/255");
                    }
                    else
                    {
                        relevantEntries.Add($"late-json:{lateFinalJsonPayload}");
                        logStore.Info("COM", $"pressure final JSON accepted: Temp={lateFinalJson.Temp}, Alco={lateFinalJson.Alco}, SYS={lateFinalJson.Sys}, DAD={lateFinalJson.Dad}");
                        logStore.Info("COM", "pressure late/tail JSON accepted after main window");
                        logStore.Info("COM", "Pressure step status: success");
                        return new PressureAndFinalJsonReadResult(
                            pressurePayload,
                            lateFinalJsonPayload,
                            "recovered-from-late-read",
                            PressureStepStatus.Success,
                            BuildRelevantSummary(relevantEntries),
                            invalidPlaceholderFinalJsonPayload);
                    }
                }

                if (invalidPlaceholderSeen)
                {
                    logStore.Warning("COM", $"port state during pressure failure: isOpen={port.IsOpen}, bytesToRead={SafeGetBytesToRead(port)}");
                    logStore.Warning("COM", $"Pressure step status: invalid-result. Relevant activity: {BuildRelevantSummary(relevantEntries)}");
                    return new PressureAndFinalJsonReadResult(
                        pressurePayload,
                        null,
                        "missing",
                        PressureStepStatus.InvalidResult,
                        BuildRelevantSummary(relevantEntries),
                        invalidPlaceholderFinalJsonPayload);
                }

                logStore.Warning("COM", "pressure wait ended with no chunks at all");
            }
            else
            {
                logStore.Warning("COM", "pressure wait ended with chunks but none accepted");
            }

            logStore.Warning("COM", $"port state during pressure failure: isOpen={port.IsOpen}, bytesToRead={SafeGetBytesToRead(port)}");
            logStore.Warning("COM", $"Pressure step status: no-data. No relevant pressure/final JSON data received within {effectivePressureTimeoutMs} ms.");
            return new PressureAndFinalJsonReadResult(
                pressurePayload,
                null,
                "missing",
                PressureStepStatus.NoData,
                "none",
                invalidPlaceholderFinalJsonPayload);
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

        private async Task<OptionalDebugReadResult> ReadOptionalMeasurementDebugAsync(
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
                    if (HasMatch(buffer, patterns))
                    {
                        return new OptionalDebugReadResult(buffer, true);
                    }
                }

                await Task.Delay(100, cancellationToken);
            }

            logStore.Warning("COM", $"ESP32 controller did not return optional {expectedLabel} within {timeoutMs} ms. Payload={buffer}");
            return new OptionalDebugReadResult(buffer, false);
        }

        private async Task<MeasurementValueReadResult> ReadMeasurementValueAsync(
            SerialPort port,
            int timeoutMs,
            CancellationToken cancellationToken,
            string stepName,
            Func<string, (bool Success, double Value, string Source)> recognizer)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var buffer = string.Empty;

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunk = port.ReadExisting();
                if (!string.IsNullOrEmpty(chunk))
                {
                    logStore.Info("COM", $"raw serial chunk received on {stepName} step: {chunk}");
                    buffer += chunk;

                    var recognized = recognizer(buffer);
                    if (recognized.Success)
                    {
                        return new MeasurementValueReadResult(buffer, recognized.Value, recognized.Source);
                    }
                }

                await Task.Delay(100, cancellationToken);
            }

            return new MeasurementValueReadResult(buffer, null, "missing");
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
            CancellationToken cancellationToken,
            string? seedBuffer = null)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var buffer = BuildFinalJsonSeedBuffer(seedBuffer);
            string? lastParseFailureReason = null;

            if (!string.IsNullOrWhiteSpace(buffer))
            {
                logStore.Info("COM", $"Final JSON buffer updated: {buffer}");
                if (TryParseFinalJsonFromAccumulatedBuffer(ref buffer, ref lastParseFailureReason, out var seededCandidate))
                {
                    return seededCandidate;
                }
            }

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunk = port.ReadExisting();
                if (!string.IsNullOrEmpty(chunk))
                {
                    logStore.Info("COM", $"Final JSON chunk received: {chunk}");
                    buffer += chunk;
                    logStore.Info("COM", $"Final JSON buffer updated: {buffer}");
                    if (TryParseFinalJsonFromAccumulatedBuffer(ref buffer, ref lastParseFailureReason, out var candidate))
                    {
                        return candidate;
                    }
                }

                await Task.Delay(100, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(lastParseFailureReason))
            {
                logStore.Warning("COM", $"Final JSON parse failed with reason: timeout after parse error ({lastParseFailureReason})");
            }

            return null;
        }

        private bool TryParseFinalJsonFromAccumulatedBuffer(ref string buffer, ref string? lastParseFailureReason, out string candidate)
        {
            candidate = string.Empty;

            if (!TryExtractJsonCandidate(buffer, out candidate, out var remainingBuffer))
            {
                return false;
            }

            logStore.Info("COM", $"Final JSON candidate detected: {candidate}");

            try
            {
                _ = ParseFinalJsonPayload(candidate);
                logStore.Info("COM", "Final JSON parse success");
                return true;
            }
            catch (Exception ex)
            {
                lastParseFailureReason = ex.Message;
                logStore.Warning("COM", $"Final JSON parse failed with reason: {ex.Message}");
                buffer = remainingBuffer;
                candidate = string.Empty;
                return false;
            }
        }

        private async Task WaitForMeasurementWindowAsync(string stage, int delayMs, CancellationToken cancellationToken)
        {
            if (delayMs <= 0)
            {
                return;
            }

            logStore.Info("COM", $"Waiting {delayMs} ms before {stage} measurement.");
            await Task.Delay(delayMs, cancellationToken);
        }

        private static bool TryExtractJsonCandidate(string buffer, out string candidate, out string remainingBuffer)
        {
            candidate = string.Empty;
            remainingBuffer = buffer;

            if (string.IsNullOrWhiteSpace(buffer))
            {
                return false;
            }

            var startIndex = buffer.IndexOf('{');
            if (startIndex < 0)
            {
                return false;
            }

            var braceDepth = 0;
            var inString = false;
            var escapeNext = false;

            for (var index = startIndex; index < buffer.Length; index++)
            {
                var current = buffer[index];

                if (escapeNext)
                {
                    escapeNext = false;
                    continue;
                }

                if (current == '\\' && inString)
                {
                    escapeNext = true;
                    continue;
                }

                if (current == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (inString)
                {
                    continue;
                }

                if (current == '{')
                {
                    braceDepth++;
                    continue;
                }

                if (current != '}')
                {
                    continue;
                }

                braceDepth--;
                if (braceDepth != 0)
                {
                    continue;
                }

                candidate = buffer.Substring(startIndex, index - startIndex + 1);
                remainingBuffer = buffer[(index + 1)..];
                return true;
            }

            remainingBuffer = buffer[startIndex..];
            return false;
        }

        private static string BuildFinalJsonSeedBuffer(string? seedBuffer)
        {
            if (string.IsNullOrWhiteSpace(seedBuffer))
            {
                return string.Empty;
            }

            var startIndex = seedBuffer.IndexOf('{');
            return startIndex >= 0
                ? seedBuffer[startIndex..]
                : string.Empty;
        }

        private void ResetPressureMeasurementState(SerialPort port)
        {
            if (!port.IsOpen)
            {
                return;
            }

            logStore.Info("COM", "Pressure measurement state checked before x1x3x without blind buffer discard.");
        }

        private static IEnumerable<string> SplitPressureChunkLines(string chunk)
        {
            var entries = chunk
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            if (entries.Count == 0 && chunk.Contains('{'))
            {
                entries.Add(chunk.Trim());
            }

            return entries;
        }

        private static PressureLineClassification ClassifyPressureLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return PressureLineClassification.Ignored;
            }

            if (line.Contains('{'))
            {
                return PressureLineClassification.JsonCandidate;
            }

            return line.Contains("NIBP", StringComparison.OrdinalIgnoreCase)
                || line.Contains("pressure", StringComparison.OrdinalIgnoreCase)
                || line.Contains("SAD", StringComparison.OrdinalIgnoreCase)
                || line.Contains("DAD", StringComparison.OrdinalIgnoreCase)
                || line.Contains("SYS", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Temp", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Alco", StringComparison.OrdinalIgnoreCase)
                ? PressureLineClassification.Relevant
                : PressureLineClassification.Ignored;
        }

        private static string GetPressureIgnoredReason(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return "empty line";
            }

            return "no pressure/debug/final-json markers detected";
        }

        private static bool IsInvalidPressurePlaceholder(FinalJsonPayload payload) =>
            payload.Sys == 255 && payload.Dad == 255;

        private static int SafeGetBytesToRead(SerialPort port)
        {
            try
            {
                return port.IsOpen ? port.BytesToRead : 0;
            }
            catch
            {
                return -1;
            }
        }

        private static string BuildRelevantSummary(IEnumerable<string> entries)
        {
            var materialized = entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .TakeLast(6)
                .ToArray();

            return materialized.Length == 0
                ? "none"
                : string.Join(" | ", materialized);
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

            if (alcohol > 4095)
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

        private static bool HasMatch(string payload, params Regex[] patterns) =>
            patterns.Any(pattern => pattern.IsMatch(payload));

        private bool TryParseFinalJsonCandidate(string buffer, ref string? lastParseFailureReason, out string candidate)
        {
            var currentFinalBuffer = BuildFinalJsonSeedBuffer(buffer);
            if (string.IsNullOrWhiteSpace(currentFinalBuffer))
            {
                candidate = string.Empty;
                return false;
            }

            logStore.Info("COM", $"Final JSON buffer updated: {currentFinalBuffer}");
            return TryParseFinalJsonFromAccumulatedBuffer(ref currentFinalBuffer, ref lastParseFailureReason, out candidate);
        }

        private (bool Success, double Value, string Source) TryRecognizeTemperatureCandidate(string payload)
        {
            if (TryParseMatchedDouble(payload, TemperatureRegexes, out var legacyTemperature))
            {
                return (true, legacyTemperature, "legacy-debug");
            }

            if (TryParseFieldValue(payload, new[] { "IRTemperature", "Temperature", "Temp" }, out var structuredTemperature))
            {
                return (true, structuredTemperature, "own-step");
            }

            if (TryParseStandaloneNumericLine(payload, value => value > 0 && value < 100, out var numericTemperature))
            {
                return (true, numericTemperature, "own-step");
            }

            return (false, default, "missing");
        }

        private (bool Success, double Value, string Source) TryRecognizeAlcoholCandidate(string payload)
        {
            if (TryParseMatchedDouble(payload, AlcoholRegexes, out var legacyAlcohol))
            {
                return (true, legacyAlcohol, "legacy-debug");
            }

            if (TryParseFieldValue(payload, new[] { "Alcohol", "Alco" }, out var structuredAlcohol))
            {
                return (true, structuredAlcohol, "own-step");
            }

            if (TryParseStandaloneNumericLine(payload, value => value > 0 && value <= InvalidAlcoholRawThreshold, out var numericAlcohol))
            {
                return (true, numericAlcohol, "own-step");
            }

            return (false, default, "missing");
        }

        private static bool TryParseFieldValue(string payload, IEnumerable<string> fieldNames, out double value)
        {
            foreach (var fieldName in fieldNames)
            {
                var regex = new Regex($@"(?<![A-Za-z])""?{Regex.Escape(fieldName)}""?\s*[:=]\s*(?<value>-?\d+(?:[.,]\d+)?)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!TryParseMatchedDouble(payload, new[] { regex }, out value))
                {
                    continue;
                }

                return true;
            }

            value = default;
            return false;
        }

        private static bool TryParseStandaloneNumericLine(string payload, Func<double, bool> isAllowedValue, out double value)
        {
            var lines = payload
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            for (var index = lines.Length - 1; index >= 0; index--)
            {
                var line = lines[index].Replace(',', '.');
                if (!Regex.IsMatch(line, @"^-?\d+(?:\.\d+)?$", RegexOptions.CultureInvariant))
                {
                    continue;
                }

                if (!double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    continue;
                }

                if (isAllowedValue(value))
                {
                    return true;
                }
            }

            value = default;
            return false;
        }

        private static double ParseMatchedDouble(string payload, IEnumerable<Regex> regexes, string fieldName)
        {
            if (!TryParseMatchedDouble(payload, regexes, out var value))
            {
                throw new InvalidOperationException($"ESP32 payload does not contain {fieldName}. Payload={payload}");
            }

            return value;
        }

        private static bool TryParseMatchedDouble(string payload, IEnumerable<Regex> regexes, out double value)
        {
            foreach (var regex in regexes)
            {
                var match = regex.Match(payload);
                if (!match.Success)
                {
                    continue;
                }

                var text = match.Groups["value"].Value.Replace(',', '.');
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    throw new InvalidOperationException($"ESP32 field value is not a valid number: {text}");
                }

                return true;
            }

            value = default;
            return false;
        }

        private static double RequireMeasurementValue(double? value, string fieldName)
        {
            if (!value.HasValue)
            {
                throw new InvalidOperationException($"Final controller JSON not received and ESP32 debug payload does not contain {fieldName}.");
            }

            return value.Value;
        }

        private (double Value, string Source) ResolveRequiredMeasurementValue(
            double? debugValue,
            string debugSource,
            double finalJsonValue,
            string finalJsonSource,
            Func<double?, bool> isRealValue,
            string fieldName)
        {
            if (debugValue.HasValue && isRealValue(debugValue))
            {
                return (debugValue.Value, debugSource);
            }

            if (isRealValue(finalJsonValue))
            {
                return (finalJsonValue, finalJsonSource);
            }

            var reason = $"{fieldName} missing or sentinel value after debug/final JSON. debug={(debugValue.HasValue ? debugValue.Value.ToString(CultureInfo.InvariantCulture) : "missing")}, finalJson={finalJsonValue.ToString(CultureInfo.InvariantCulture)}";
            logStore.Error("COM", $"detected missing required measurement: {fieldName}");
            logStore.Error("COM", $"measurement flow failure reason: {reason}");
            throw new InvalidOperationException($"measurement flow failure reason: {reason}");
        }

        private (double Value, string Source) ResolveTemperatureMeasurementValue(
            double? debugValue,
            string debugSource,
            double finalJsonValue,
            string finalJsonSource)
        {
            if (HasReceivedTemperatureValue(debugValue))
            {
                logStore.Info("COM", $"temperature preserved from {debugSource}: {debugValue.Value.ToString("F2", CultureInfo.InvariantCulture)}");
                return (debugValue.Value, debugSource);
            }

            if (HasReceivedTemperatureValue(finalJsonValue))
            {
                logStore.Info("COM", $"temperature candidate recognized from final JSON: {finalJsonValue.ToString("F2", CultureInfo.InvariantCulture)}");
                logStore.Info("COM", $"final JSON temperature accepted: {finalJsonValue.ToString("F2", CultureInfo.InvariantCulture)}");
                logStore.Info("COM", "temperature value classified as received, not missing");
                logStore.Info("COM", "HasTemperatureValue set from final JSON");
                return (finalJsonValue, finalJsonSource);
            }

            var reason = $"temperature missing after debug/final JSON. debug={(debugValue.HasValue ? debugValue.Value.ToString(CultureInfo.InvariantCulture) : "missing")}, finalJson={finalJsonValue.ToString(CultureInfo.InvariantCulture)}";
            logStore.Error("COM", "detected missing required measurement: temperature");
            logStore.Error("COM", $"measurement flow failure reason: {reason}");
            throw new InvalidOperationException($"measurement flow failure reason: {reason}");
        }

        private (double Value, string Source) ResolveAlcoholMeasurementValue(
            double? debugValue,
            string debugSource,
            double finalJsonValue,
            string finalJsonSource)
        {
            if (HasReceivedAlcoholValue(debugValue))
            {
                logStore.Info("COM", $"alcohol preserved from {debugSource}: {debugValue.Value.ToString("F0", CultureInfo.InvariantCulture)}");
                return (debugValue.Value, debugSource);
            }

            if (HasReceivedAlcoholValue(finalJsonValue))
            {
                logStore.Info("COM", $"alcohol candidate recognized from final JSON: {finalJsonValue.ToString("F0", CultureInfo.InvariantCulture)}");
                logStore.Info("COM", $"final JSON alcohol accepted: {finalJsonValue.ToString("F0", CultureInfo.InvariantCulture)}");
                logStore.Info("COM", "alcohol value classified as received, not missing");
                logStore.Info("COM", "HasAlcoholValue set from final JSON");
                return (finalJsonValue, finalJsonSource);
            }

            var reason = $"alcohol missing after debug/final JSON. debug={(debugValue.HasValue ? debugValue.Value.ToString(CultureInfo.InvariantCulture) : "missing")}, finalJson={finalJsonValue.ToString(CultureInfo.InvariantCulture)}";
            logStore.Error("COM", "detected missing required measurement: alcohol");
            logStore.Error("COM", $"measurement flow failure reason: {reason}");
            throw new InvalidOperationException($"measurement flow failure reason: {reason}");
        }

        private static bool IsRealTemperatureValue(double? value) =>
            value.HasValue && value.Value > MinimumHumanTemperature;

        private static bool HasReceivedTemperatureValue(double? value) =>
            value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value);

        private static bool IsRealAlcoholValue(double? value) =>
            value.HasValue && value.Value > 0 && value.Value <= InvalidAlcoholRawThreshold;

        private static bool HasReceivedAlcoholValue(double? value) =>
            value.HasValue && value.Value > 0 && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value);

        private void EnsureRealPressureValues(double sys, double dad, string source)
        {
            if (sys > 0 && dad > 0 && sys != 255 && dad != 255)
            {
                return;
            }

            var reason = $"pressure missing or sentinel value from {source}. SYS={sys.ToString(CultureInfo.InvariantCulture)}, DAD={dad.ToString(CultureInfo.InvariantCulture)}";
            logStore.Error("COM", "detected missing required measurement: pressure");
            logStore.Error("COM", $"measurement flow failure reason: {reason}");
            throw new InvalidOperationException($"measurement flow failure reason: {reason}");
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

        private enum PressureStepStatus
        {
            Success,
            InvalidResult,
            Timeout,
            NoData
        }

        private enum PressureLineClassification
        {
            Ignored,
            Relevant,
            JsonCandidate
        }

        private sealed record ControllerResponse(double Temp, string TemperatureSource, double Alco, bool HasAlcoholValue, string AlcoholSource, bool HasTemperatureValue, double Sys, double Dad, bool HasPressureValue, string PressureSource, string SourceLabel, string ResultSummary);
        private sealed record FinalJsonPayload(double Temp, double Alco, double Sys, double Dad);
        private sealed record PressureAndFinalJsonReadResult(string PressurePayload, string? FinalJsonPayload, string FinalJsonSource, PressureStepStatus Status, string RelevantSummary, string? InvalidPlaceholderFinalJsonPayload);
        private sealed record OptionalDebugReadResult(string Payload, bool Matched);
        private sealed record MeasurementValueReadResult(string Payload, double? Value, string Source);

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
            public int TemperaturePreparationDelayMs { get; init; } = 2500;
            public int TemperatureRetryDelayMs { get; init; } = 1500;
            public int AlcoholPreparationDelayMs { get; init; } = 2500;
            public int AlcoholRetryDelayMs { get; init; } = 2000;
            public int PressurePreparationDelayMs { get; init; } = 20000;
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
