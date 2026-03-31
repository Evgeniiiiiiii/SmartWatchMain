using System;
using System.IO.Ports;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace SmartWatchProj.Cli
{
    internal static class ComSmokeTestRunner
    {
        public static bool TryRun(string[] args, out int exitCode)
        {
            exitCode = 0;

            if (args.Length == 0)
            {
                return false;
            }

            if (HasFlag(args, "--list-com"))
            {
                exitCode = ListPorts(HasFlag(args, "--json"));
                return true;
            }

            if (HasFlag(args, "--com-smoke"))
            {
                exitCode = RunSmokeTest(args);
                return true;
            }

            return false;
        }

        private static int ListPorts(bool json)
        {
            var ports = SerialPort.GetPortNames()
                .OrderBy(port => port, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    new { ports },
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                Console.WriteLine(ports.Length == 0
                    ? "COM ports: none"
                    : $"COM ports: {string.Join(", ", ports)}");
            }

            return 0;
        }

        private static int RunSmokeTest(string[] args)
        {
            var portName = GetOption(args, "--port");
            if (string.IsNullOrWhiteSpace(portName))
            {
                PrintUsage();
                return 2;
            }

            var message = GetOption(args, "--message") ?? "PING";
            var baudRate = GetIntOption(args, "--baud", 9600);
            var timeoutMs = GetIntOption(args, "--timeout", 1500);
            var outputJson = HasFlag(args, "--json");
            var readLine = HasFlag(args, "--read-line");
            var rawWrite = HasFlag(args, "--raw");
            var newline = DecodeEscapes(GetOption(args, "--newline") ?? "\\n");

            try
            {
                using var port = new SerialPort(portName, baudRate)
                {
                    ReadTimeout = timeoutMs,
                    WriteTimeout = timeoutMs,
                    NewLine = newline
                };

                port.Open();

                if (!string.IsNullOrEmpty(message))
                {
                    if (rawWrite)
                    {
                        port.Write(message);
                    }
                    else
                    {
                        port.WriteLine(message);
                    }
                }

                Thread.Sleep(Math.Min(timeoutMs, 250));

                string response;
                try
                {
                    response = readLine ? port.ReadLine() : port.ReadExisting();
                }
                catch (TimeoutException)
                {
                    response = string.Empty;
                }

                if (outputJson)
                {
                    Console.WriteLine(JsonSerializer.Serialize(
                        new
                        {
                            port = portName,
                            baud = baudRate,
                            sent = message,
                            response,
                            responseLength = response.Length
                        },
                        new JsonSerializerOptions { WriteIndented = true }));
                }
                else
                {
                    Console.WriteLine($"Port: {portName}");
                    Console.WriteLine($"Baud: {baudRate}");
                    Console.WriteLine($"Sent: {message}");
                    Console.WriteLine(string.IsNullOrEmpty(response)
                        ? "Response: <empty>"
                        : $"Response: {response}");
                }

                return 0;
            }
            catch (Exception ex)
            {
                if (outputJson)
                {
                    Console.WriteLine(JsonSerializer.Serialize(
                        new
                        {
                            port = portName,
                            error = ex.Message
                        },
                        new JsonSerializerOptions { WriteIndented = true }));
                }
                else
                {
                    Console.WriteLine($"COM smoke-test failed: {ex.Message}");
                }

                return 1;
            }
        }

        private static bool HasFlag(string[] args, string flag) =>
            args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));

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

        private static int GetIntOption(string[] args, string option, int fallback)
        {
            var value = GetOption(args, option);
            return int.TryParse(value, out var parsed) ? parsed : fallback;
        }

        private static string DecodeEscapes(string value) =>
            value
                .Replace("\\r", "\r", StringComparison.Ordinal)
                .Replace("\\n", "\n", StringComparison.Ordinal)
                .Replace("\\t", "\t", StringComparison.Ordinal);

        private static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  SmartWatchProj --list-com [--json]");
            Console.WriteLine("  SmartWatchProj --com-smoke --port COM3 [--baud 9600] [--message PING] [--timeout 1500] [--read-line] [--raw] [--json]");
        }
    }
}
