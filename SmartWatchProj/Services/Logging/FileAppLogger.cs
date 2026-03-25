using System;
using System.IO;
using System.Text;

namespace SmartWatchProj.Services.Logging
{
    public sealed class FileAppLogger : IAppLogger
    {
        private readonly object _syncRoot = new();

        public FileAppLogger()
        {
            var logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            LogFilePath = Path.Combine(logDirectory, $"smartwatch-{DateTime.Now:yyyyMMdd}.log");
        }

        public string LogFilePath { get; }

        public void Info(string message)
        {
            Write("INFO", message, null);
        }

        public void Warning(string message)
        {
            Write("WARN", message, null);
        }

        public void Error(string message, Exception? exception = null)
        {
            Write("ERROR", message, exception);
        }

        private void Write(string level, string message, Exception? exception)
        {
            var builder = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [")
                .Append(level)
                .Append("] ")
                .Append(message);

            if (exception != null)
            {
                builder
                    .Append(" | ")
                    .Append(exception.GetType().Name)
                    .Append(": ")
                    .Append(exception.Message);
            }

            var line = builder.ToString();

            lock (_syncRoot)
            {
                File.AppendAllText(LogFilePath, line + Environment.NewLine, Encoding.UTF8);
            }

            Console.WriteLine(line);
        }
    }
}
