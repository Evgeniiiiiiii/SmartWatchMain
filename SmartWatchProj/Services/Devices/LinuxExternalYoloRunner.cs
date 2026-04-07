using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace SmartWatchProj.Services.Devices
{
    internal static class LinuxExternalYoloRunner
    {
        private const float ConfidenceThreshold = 0.25f;
        private const float NmsThreshold = 0.45f;

        public static bool TryRunCli(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (!args.Any(arg => string.Equals(arg, "--linux-yolo-worker", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            exitCode = RunWorker(args);
            return true;
        }

        public static LinuxExternalYoloResult Run(SKBitmap bitmap, string modelPath, int timeoutMs = 15000)
        {
            var tempImagePath = Path.Combine(Path.GetTempPath(), $"smartwatch-linux-yolo-{Guid.NewGuid():N}.png");
            var tempLogPath = Path.Combine(Path.GetTempPath(), $"smartwatch-linux-yolo-worker-{Guid.NewGuid():N}.log");

            try
            {
                using (var image = SKImage.FromBitmap(bitmap))
                using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
                using (var stream = File.OpenWrite(tempImagePath))
                {
                    data.SaveTo(stream);
                }

                var startInfo = BuildWorkerStartInfo(tempImagePath, modelPath, tempLogPath);
                using var process = new Process { StartInfo = startInfo };
                process.Start();

                var standardOutput = process.StandardOutput.ReadToEnd();
                var standardError = process.StandardError.ReadToEnd();

                if (!process.WaitForExit(timeoutMs))
                {
                    TryKill(process);
                    return LinuxExternalYoloResult.Failed($"Linux external inference timed out. Worker log={tempLogPath}. {ReadLogTail(tempLogPath)}");
                }

                if (process.ExitCode != 0)
                {
                    var errorBuilder = new StringBuilder();
                    errorBuilder.Append($"Linux external inference process exited with code {process.ExitCode}. Worker log={tempLogPath}.");
                    if (!string.IsNullOrWhiteSpace(standardError))
                    {
                        errorBuilder.Append(' ').Append(standardError.Trim());
                    }

                    var logTail = ReadLogTail(tempLogPath);
                    if (!string.IsNullOrWhiteSpace(logTail))
                    {
                        errorBuilder.Append(' ').Append(logTail);
                    }

                    return LinuxExternalYoloResult.Failed(errorBuilder.ToString());
                }

                if (string.IsNullOrWhiteSpace(standardOutput))
                {
                    return LinuxExternalYoloResult.Failed($"Linux external inference returned empty output. Worker log={tempLogPath}. {ReadLogTail(tempLogPath)}");
                }

                var payload = JsonSerializer.Deserialize<LinuxExternalYoloWorkerPayload>(standardOutput, JsonOptions);
                if (payload is null)
                {
                    return LinuxExternalYoloResult.Failed($"Linux external inference returned unreadable JSON. Worker log={tempLogPath}. {ReadLogTail(tempLogPath)}");
                }

                return new LinuxExternalYoloResult(
                    payload.Error is null,
                    payload.PersonCount,
                    payload.MaxConfidence,
                    payload.Detections ?? Array.Empty<LinuxExternalYoloDetection>(),
                    payload.Error,
                    tempLogPath);
            }
            catch (Exception ex)
            {
                return LinuxExternalYoloResult.Failed($"Linux external inference failed safely: {ex.Message}. Worker log={tempLogPath}");
            }
            finally
            {
                TryDelete(tempImagePath);
            }
        }

        private static ProcessStartInfo BuildWorkerStartInfo(string imagePath, string modelPath, string logPath)
        {
            var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location ?? string.Empty;
            var processPath = Environment.ProcessPath ?? entryAssemblyPath;
            var startInfo = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetExtension(entryAssemblyPath), ".dll", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.FileName = processPath;
                startInfo.ArgumentList.Add(entryAssemblyPath);
            }
            else
            {
                startInfo.FileName = processPath;
            }

            startInfo.ArgumentList.Add("--linux-yolo-worker");
            startInfo.ArgumentList.Add("--image");
            startInfo.ArgumentList.Add(imagePath);
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(modelPath);
            startInfo.ArgumentList.Add("--log");
            startInfo.ArgumentList.Add(logPath);
            startInfo.ArgumentList.Add("--json");
            return startInfo;
        }

        private static int RunWorker(string[] args)
        {
            var imagePath = GetOption(args, "--image");
            var modelPath = GetOption(args, "--model");
            var logPath = GetOption(args, "--log") ?? Path.Combine(Path.GetTempPath(), $"smartwatch-linux-yolo-worker-{Guid.NewGuid():N}.log");
            var json = args.Any(arg => string.Equals(arg, "--json", StringComparison.OrdinalIgnoreCase));

            using var logger = new WorkerLogger(logPath);
            logger.Log("worker started");
            logger.Log($"image path resolved: {imagePath ?? "<null>"}");
            logger.Log($"model path resolved: {modelPath ?? "<null>"}");

            if (string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(modelPath))
            {
                logger.Log("missing required arguments");
                WritePayload(new LinuxExternalYoloWorkerPayload(0, 0, Array.Empty<LinuxExternalYoloDetection>(), "Missing --image or --model.", logPath), json);
                return 2;
            }

            try
            {
                logger.Log("frame load started");
                using var bitmap = SKBitmap.Decode(imagePath);
                if (bitmap is null || bitmap.IsEmpty)
                {
                    logger.Log("frame load failed");
                    WritePayload(new LinuxExternalYoloWorkerPayload(0, 0, Array.Empty<LinuxExternalYoloDetection>(), $"Failed to decode image {imagePath}.", logPath), json);
                    return 3;
                }

                logger.Log($"frame loaded: {bitmap.Width}x{bitmap.Height}; colorType={bitmap.ColorType}; alphaType={bitmap.AlphaType}");
                logger.Log("model load started");
                logger.Log("before session options");
                using var sessionOptions = new SessionOptions();
                logger.Log("after session options");
                sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                sessionOptions.InterOpNumThreads = 1;
                sessionOptions.IntraOpNumThreads = 1;
                sessionOptions.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING;
                logger.Log("before provider init");
                sessionOptions.AppendExecutionProvider_CPU();
                logger.Log("after provider init");
                logger.Log("before InferenceSession");
                using var session = new InferenceSession(modelPath, sessionOptions);
                logger.Log("after InferenceSession");
                logger.Log("model loaded");

                var inputMetadata = session.InputMetadata.First();
                var inputName = inputMetadata.Key;
                var inputDimensions = ResolveInputDimensions(inputMetadata.Value.Dimensions);
                logger.Log($"input resolved: name={inputName}; dims=[{string.Join(",", inputDimensions)}]");

                logger.Log("frame converted");
                using var inputBitmap = PrepareInputBitmap(bitmap, inputDimensions.Width, inputDimensions.Height, out var scale, out var padX, out var padY);
                var inputTensor = CreateTensorFromBitmap(inputBitmap);

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
                };

                logger.Log("inference started");
                using var results = session.Run(inputs);
                logger.Log("inference finished");

                var detections = ParseDetections(results, bitmap.Width, bitmap.Height, scale, padX, padY, logger);
                var personDetections = ApplyNms(detections
                        .Where(item => string.Equals(item.Label, "person", StringComparison.OrdinalIgnoreCase))
                        .ToList(),
                    NmsThreshold);

                logger.Log($"postprocess finished: personCount={personDetections.Count}");
                var payload = new LinuxExternalYoloWorkerPayload(
                    personDetections.Count,
                    personDetections.Count == 0 ? 0 : personDetections.Max(item => item.Confidence),
                    personDetections.ToArray(),
                    null,
                    logPath);
                WritePayload(payload, json);
                logger.Log("worker completed successfully");
                return 0;
            }
            catch (Exception ex)
            {
                logger.Log($"worker exception: {ex.GetType().FullName}: {ex.Message}");
                WritePayload(new LinuxExternalYoloWorkerPayload(0, 0, Array.Empty<LinuxExternalYoloDetection>(), ex.Message, logPath), json);
                return 1;
            }
        }

        private static (int Width, int Height) ResolveInputDimensions(IReadOnlyList<int> dimensions)
        {
            var width = dimensions.Count >= 4 && dimensions[3] > 0 ? dimensions[3] : 640;
            var height = dimensions.Count >= 3 && dimensions[2] > 0 ? dimensions[2] : 640;
            return (width, height);
        }

        private static SKBitmap PrepareInputBitmap(SKBitmap source, int targetWidth, int targetHeight, out float scale, out float padX, out float padY)
        {
            scale = Math.Min((float)targetWidth / source.Width, (float)targetHeight / source.Height);
            var resizedWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
            var resizedHeight = Math.Max(1, (int)Math.Round(source.Height * scale));
            padX = (targetWidth - resizedWidth) / 2f;
            padY = (targetHeight - resizedHeight) / 2f;

            var resized = source.Resize(new SKImageInfo(resizedWidth, resizedHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul), SKSamplingOptions.Default)
                ?? throw new InvalidOperationException("Failed to resize bitmap for inference.");

            var canvasBitmap = new SKBitmap(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var canvas = new SKCanvas(canvasBitmap);
            canvas.Clear(SKColors.Black);
            canvas.DrawBitmap(resized, padX, padY);
            resized.Dispose();
            return canvasBitmap;
        }

        private static DenseTensor<float> CreateTensorFromBitmap(SKBitmap bitmap)
        {
            var tensor = new DenseTensor<float>(new[] { 1, 3, bitmap.Height, bitmap.Width });
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var color = bitmap.GetPixel(x, y);
                    tensor[0, 0, y, x] = color.Red / 255f;
                    tensor[0, 1, y, x] = color.Green / 255f;
                    tensor[0, 2, y, x] = color.Blue / 255f;
                }
            }

            return tensor;
        }

        private static List<LinuxExternalYoloDetection> ParseDetections(
            IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results,
            int originalWidth,
            int originalHeight,
            float scale,
            float padX,
            float padY,
            WorkerLogger logger)
        {
            var output = results.FirstOrDefault()
                ?? throw new InvalidOperationException("Inference result collection is empty.");

            var tensor = output.AsTensor<float>();
            var dimensions = tensor.Dimensions.ToArray();
            logger.Log($"output tensor dims=[{string.Join(",", dimensions)}]");

            return dimensions.Length switch
            {
                3 => ParseYoloOutput3D(tensor, dimensions, originalWidth, originalHeight, scale, padX, padY, logger),
                _ => throw new InvalidOperationException($"Unsupported output tensor dimensions: [{string.Join(",", dimensions)}]")
            };
        }

        private static List<LinuxExternalYoloDetection> ParseYoloOutput3D(
            Microsoft.ML.OnnxRuntime.Tensors.Tensor<float> tensor,
            int[] dimensions,
            int originalWidth,
            int originalHeight,
            float scale,
            float padX,
            float padY,
            WorkerLogger logger)
        {
            var detections = new List<LinuxExternalYoloDetection>();
            var dim1 = dimensions[1];
            var dim2 = dimensions[2];
            var channelFirst = dim1 <= 128 && dim2 > dim1;

            var featureCount = channelFirst ? dim1 : dim2;
            var candidateCount = channelFirst ? dim2 : dim1;
            logger.Log($"parse output: channelFirst={channelFirst}; featureCount={featureCount}; candidateCount={candidateCount}");

            for (var candidateIndex = 0; candidateIndex < candidateCount; candidateIndex++)
            {
                float cx;
                float cy;
                float width;
                float height;
                float confidence;
                var classOffset = featureCount == 85 ? 5 : 4;

                if (channelFirst)
                {
                    cx = tensor[0, 0, candidateIndex];
                    cy = tensor[0, 1, candidateIndex];
                    width = tensor[0, 2, candidateIndex];
                    height = tensor[0, 3, candidateIndex];
                    confidence = featureCount == 85
                        ? tensor[0, 4, candidateIndex] * tensor[0, 5, candidateIndex]
                        : tensor[0, 4, candidateIndex];
                }
                else
                {
                    cx = tensor[0, candidateIndex, 0];
                    cy = tensor[0, candidateIndex, 1];
                    width = tensor[0, candidateIndex, 2];
                    height = tensor[0, candidateIndex, 3];
                    confidence = featureCount == 85
                        ? tensor[0, candidateIndex, 4] * tensor[0, candidateIndex, 5]
                        : tensor[0, candidateIndex, 4];
                }

                if (confidence < ConfidenceThreshold)
                {
                    continue;
                }

                var left = (cx - width / 2f - padX) / scale;
                var top = (cy - height / 2f - padY) / scale;
                var actualWidth = width / scale;
                var actualHeight = height / scale;

                left = Math.Clamp(left, 0, originalWidth);
                top = Math.Clamp(top, 0, originalHeight);
                actualWidth = Math.Clamp(actualWidth, 0, originalWidth - left);
                actualHeight = Math.Clamp(actualHeight, 0, originalHeight - top);

                if (actualWidth <= 1 || actualHeight <= 1)
                {
                    continue;
                }

                detections.Add(new LinuxExternalYoloDetection("person", confidence, left, top, actualWidth, actualHeight));
            }

            logger.Log($"parse output finished: rawPersonCandidates={detections.Count}");
            return detections;
        }

        private static List<LinuxExternalYoloDetection> ApplyNms(List<LinuxExternalYoloDetection> detections, float threshold)
        {
            var ordered = detections
                .OrderByDescending(item => item.Confidence)
                .ToList();
            var kept = new List<LinuxExternalYoloDetection>();

            while (ordered.Count > 0)
            {
                var current = ordered[0];
                kept.Add(current);
                ordered.RemoveAt(0);
                ordered.RemoveAll(candidate => IoU(current, candidate) >= threshold);
            }

            return kept;
        }

        private static float IoU(LinuxExternalYoloDetection a, LinuxExternalYoloDetection b)
        {
            var x1 = Math.Max(a.X, b.X);
            var y1 = Math.Max(a.Y, b.Y);
            var x2 = Math.Min(a.X + a.Width, b.X + b.Width);
            var y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

            var intersectionWidth = Math.Max(0, x2 - x1);
            var intersectionHeight = Math.Max(0, y2 - y1);
            var intersection = intersectionWidth * intersectionHeight;
            if (intersection <= 0)
            {
                return 0;
            }

            var union = a.Width * a.Height + b.Width * b.Height - intersection;
            return union <= 0 ? 0 : intersection / union;
        }

        private static string? GetOption(string[] args, string option)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return null;
        }

        private static void WritePayload(LinuxExternalYoloWorkerPayload payload, bool json)
        {
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
                return;
            }

            Console.WriteLine($"PersonCount={payload.PersonCount}; MaxConfidence={payload.MaxConfidence:0.00}; Error={payload.Error ?? "<none>"}; LogPath={payload.LogPath}");
        }

        private static void TryKill(Process process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static string ReadLogTail(string logPath)
        {
            try
            {
                if (!File.Exists(logPath))
                {
                    return "Worker log file not found.";
                }

                var lines = File.ReadLines(logPath).TakeLast(12);
                return "Worker log tail: " + string.Join(" | ", lines);
            }
            catch (Exception ex)
            {
                return $"Failed to read worker log: {ex.Message}";
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        private sealed class WorkerLogger : IDisposable
        {
            private readonly StreamWriter writer;

            public WorkerLogger(string path)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Path.GetTempPath());
                writer = new StreamWriter(File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true
                };
                Log($"log path: {path}");
            }

            public void Log(string message) =>
                writer.WriteLine($"[{DateTime.UtcNow:O}] {message}");

            public void Dispose() => writer.Dispose();
        }
    }

    internal sealed record LinuxExternalYoloResult(bool Success, int PersonCount, double MaxConfidence, IReadOnlyList<LinuxExternalYoloDetection> Detections, string? Error, string? LogPath)
    {
        public static LinuxExternalYoloResult Failed(string error) =>
            new(false, 0, 0, Array.Empty<LinuxExternalYoloDetection>(), error, null);
    }

    internal sealed record LinuxExternalYoloDetection(string Label, double Confidence, float X, float Y, float Width, float Height);

    internal sealed record LinuxExternalYoloWorkerPayload(int PersonCount, double MaxConfidence, LinuxExternalYoloDetection[] Detections, string? Error, string? LogPath);
}
