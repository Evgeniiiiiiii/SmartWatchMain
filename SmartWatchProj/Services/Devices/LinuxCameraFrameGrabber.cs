using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace SmartWatchProj.Services.Devices
{
    internal static class LinuxCameraFrameGrabber
    {
        public static bool TryGrabFrame(out SKBitmap? bitmap, out string? devicePath, out string? error)
        {
            bitmap = null;
            error = null;
            devicePath = null;

            if (!OperatingSystem.IsLinux())
            {
                error = "Linux camera grabber is available only on Linux.";
                return false;
            }

            var devices = Directory.Exists("/dev")
                ? Directory.GetFiles("/dev", "video*").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();

            devicePath = devices.FirstOrDefault(path => string.Equals(path, "/dev/video0", StringComparison.Ordinal))
                ?? devices.FirstOrDefault();

            if (string.IsNullOrWhiteSpace(devicePath))
            {
                error = "No Linux video devices found.";
                return false;
            }

            try
            {
                var bytes = RunFfmpegFrameGrab(devicePath, out var ffmpegError);
                if (bytes is null || bytes.Length == 0)
                {
                    error = string.IsNullOrWhiteSpace(ffmpegError)
                        ? $"ffmpeg did not return frame bytes for {devicePath}."
                        : $"ffmpeg failed for {devicePath}: {ffmpegError}";
                    return false;
                }

                bitmap = SKBitmap.Decode(bytes);
                if (bitmap is null || bitmap.IsEmpty)
                {
                    error = $"Failed to decode Linux camera frame from {devicePath}.";
                    bitmap?.Dispose();
                    bitmap = null;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().FullName}: {ex.Message}";
                bitmap?.Dispose();
                bitmap = null;
                return false;
            }
        }

        private static byte[]? RunFfmpegFrameGrab(string devicePath, out string? error)
        {
            error = null;

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-hide_banner -loglevel error -f video4linux2 -i {devicePath} -frames:v 1 -f image2pipe -vcodec png -",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                error = $"Failed to start ffmpeg: {ex.Message}";
                return null;
            }

            using var output = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(output);
            var stdErr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                error = "ffmpeg timed out while reading Linux camera frame.";
                return null;
            }

            if (process.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(stdErr)
                    ? $"ffmpeg exit code {process.ExitCode}"
                    : stdErr.Trim();
                return null;
            }

            return output.ToArray();
        }
    }
}
