using System.Collections.Generic;

namespace SmartWatchProj.Models.Devices
{
    public sealed class DeviceConfigurationFile
    {
        public List<DeviceModuleState> Devices { get; set; } = new();
    }
}
