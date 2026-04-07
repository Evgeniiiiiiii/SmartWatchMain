using Avalonia.Media;

namespace SmartWatchProj.Models.Devices
{
    public sealed class DeviceStatusSnapshot
    {
        public string Id { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public string? StatusLabel { get; init; }
        public string? BlockingLabel { get; init; }
        public DeviceReadinessState State { get; init; }
        public bool IsBlocking { get; init; }

        public string StateText => State switch
        {
            DeviceReadinessState.Ready => "ready",
            DeviceReadinessState.Warning => "warning",
            DeviceReadinessState.Missing => "missing",
            DeviceReadinessState.Error => "error",
            DeviceReadinessState.Diagnostics => "fallback",
            DeviceReadinessState.Unavailable => "unavailable",
            DeviceReadinessState.Skipped => "skipped",
            DeviceReadinessState.Disabled => "disabled",
            _ => "unknown"
        };

        public IBrush AccentBrush => State switch
        {
            DeviceReadinessState.Ready => Brushes.ForestGreen,
            DeviceReadinessState.Warning => Brushes.DarkOrange,
            DeviceReadinessState.Missing => Brushes.IndianRed,
            DeviceReadinessState.Error => Brushes.Firebrick,
            DeviceReadinessState.Diagnostics => Brushes.SteelBlue,
            DeviceReadinessState.Unavailable => Brushes.SlateGray,
            DeviceReadinessState.Skipped => Brushes.DarkSlateBlue,
            DeviceReadinessState.Disabled => Brushes.Gray,
            _ => Brushes.DimGray
        };

        public string BlockingText => BlockingLabel ?? (IsBlocking
            ? "Блокирует реальный запуск"
            : State switch
            {
                DeviceReadinessState.Diagnostics => "Доступен только diagnostics fallback",
                DeviceReadinessState.Skipped => "Проверка пропущена",
                DeviceReadinessState.Disabled => "Проверка отключена",
                _ => "Не блокирует diagnostics"
            });
    }
}
