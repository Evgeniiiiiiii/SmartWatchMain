using System;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;

namespace SmartWatchProj.Services.Devices
{
    public static class SerialSmokeTestRunner
    {
        public static async Task<bool> TryRunAsync(string[] args)
        {
            if (args.Length == 0)
            {
                return false;
            }

            if (HasFlag(args, "--list-ports"))
            {
                await ListPortsAsync().ConfigureAwait(false);
                return true;
            }

            var serialTestIndex = Array.FindIndex(args, arg => string.Equals(arg, "--serial-test", StringComparison.OrdinalIgnoreCase));
            if (serialTestIndex < 0)
            {
                return false;
            }

            if (args.Length <= serialTestIndex + 2)
            {
                PrintUsage();
                return true;
            }

            var portName = args[serialTestIndex + 1];
            var command = args[serialTestIndex + 2];
            var baudRate = GetIntOption(args, "--baud", 115200);
            var timeoutMs = GetIntOption(args, "--timeout-ms", 10000);
            var appendNewLine = HasFlag(args, "--newline");

            await RunSerialTestAsync(portName, command, baudRate, timeoutMs, appendNewLine).ConfigureAwait(false);
            return true;
        }

        private static async Task ListPortsAsync()
        {
            await using var transport = new SerialPortDeviceTransport();
            var ports = transport.GetAvailablePorts();

            Console.WriteLine("Available COM ports:");
            if (ports.Count == 0)
            {
                Console.WriteLine("  <none>");
                return;
            }

            foreach (var port in ports)
            {
                Console.WriteLine($"  {port}");
            }
        }

        private static async Task RunSerialTestAsync(string portName, string command, int baudRate, int timeoutMs, bool appendNewLine)
        {
            var options = new SerialPortConnectionOptions
            {
                PortName = portName,
                BaudRate = baudRate,
                ResponseTimeout = TimeSpan.FromMilliseconds(timeoutMs)
            };

            await using var transport = new SerialPortDeviceTransport();
            transport.RawChunkReceived += (_, chunk) =>
            {
                Console.WriteLine($"[RX chunk] {EscapeForConsole(chunk)}");
            };
            transport.JsonMessageReceived += (_, json) =>
            {
                Console.WriteLine("[RX json]");
                Console.WriteLine(PrettyJson(json));
            };

            Console.WriteLine($"Opening {portName} @ {baudRate}...");
            await transport.OpenAsync(options).ConfigureAwait(false);

            try
            {
                Console.WriteLine($"Sending: {command}");
                var json = await transport.SendAndReceiveJsonAsync(command, options.ResponseTimeout, appendNewLine).ConfigureAwait(false);
                Console.WriteLine("Final response:");
                Console.WriteLine(PrettyJson(json));
            }
            finally
            {
                await transport.CloseAsync().ConfigureAwait(false);
                Console.WriteLine("Port closed.");
            }
        }

        private static bool HasFlag(string[] args, string flag)
        {
            return Array.Exists(args, arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));
        }

        private static int GetIntOption(string[] args, string optionName, int defaultValue)
        {
            var index = Array.FindIndex(args, arg => string.Equals(arg, optionName, StringComparison.OrdinalIgnoreCase));
            if (index < 0 || args.Length <= index + 1)
            {
                return defaultValue;
            }

            return int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : defaultValue;
        }

        private static string PrettyJson(string json)
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }

        private static string EscapeForConsole(string chunk)
        {
            return chunk
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  --list-ports");
            Console.WriteLine("  --serial-test <COMx> <command> [--baud 115200] [--timeout-ms 10000] [--newline]");
        }
    }
}
