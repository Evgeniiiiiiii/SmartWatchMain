namespace SmartWatchProj.Models.Devices
{
    public enum DeviceReadinessState
    {
        Unknown,
        Ready,
        Warning,
        Missing,
        Error,
        Diagnostics,
        Unavailable,
        Skipped,
        Disabled
    }
}
