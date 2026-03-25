namespace SmartWatchProj.Models.Devices
{
    public sealed class DeviceOperationResult
    {
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public DeviceModuleType ModuleType { get; set; }
        public bool Success { get; set; }
        public DeviceStatus Status { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? PortName { get; set; }
        public string? JsonResponse { get; set; }
        public PrimaryStationResponse? PrimaryStationResponse { get; set; }
        public WearableDeviceResponse? WearableDeviceResponse { get; set; }
    }
}
