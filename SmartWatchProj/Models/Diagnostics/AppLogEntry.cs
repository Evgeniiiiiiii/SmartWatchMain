using System;
using Avalonia.Media;

namespace SmartWatchProj.Models.Diagnostics
{
    public sealed class AppLogEntry
    {
        public DateTime Timestamp { get; init; } = DateTime.Now;
        public string Level { get; init; } = "INFO";
        public string Source { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;

        public string Summary => $"{Timestamp:HH:mm:ss} [{Level}] {Source}: {Message}";

        public IBrush AccentBrush => Level switch
        {
            "ERROR" => Brushes.Firebrick,
            "WARN" => Brushes.DarkOrange,
            _ => Brushes.DarkSlateGray
        };
    }
}
