using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SmartWatchProj.Models.Diagnostics;

namespace SmartWatchProj.Services.Diagnostics
{
    public sealed class RuntimeLogStore
    {
        private readonly object syncRoot = new();
        private readonly List<AppLogEntry> entries = new();
        private readonly string logFilePath;

        public RuntimeLogStore(string baseDirectory)
        {
            var logDirectory = Path.Combine(baseDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            logFilePath = Path.Combine(logDirectory, "runtime.log");
        }

        public string LogFilePath => logFilePath;

        public void Info(string source, string message) => Write("INFO", source, message);

        public void Warning(string source, string message) => Write("WARN", source, message);

        public void Error(string source, string message) => Write("ERROR", source, message);

        public IReadOnlyList<AppLogEntry> GetRecentEntries(int maxCount = 20)
        {
            lock (syncRoot)
            {
                return entries.TakeLast(maxCount).ToList();
            }
        }

        private void Write(string level, string source, string message)
        {
            var entry = new AppLogEntry
            {
                Timestamp = DateTime.Now,
                Level = level,
                Source = source,
                Message = message
            };

            lock (syncRoot)
            {
                entries.Add(entry);
                if (entries.Count > 200)
                {
                    entries.RemoveRange(0, entries.Count - 200);
                }
            }

            Console.WriteLine(entry.Summary);

            try
            {
                File.AppendAllText(
                    logFilePath,
                    $"{entry.Timestamp:O}\t{level}\t{source}\t{message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch
            {
                // Logging should never break the operator flow.
            }
        }
    }
}
