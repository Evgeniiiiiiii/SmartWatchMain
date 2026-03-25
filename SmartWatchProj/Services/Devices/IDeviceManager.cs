using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using SmartWatchProj.Models.Devices;

namespace SmartWatchProj.Services.Devices
{
    public interface IDeviceManager
    {
        ObservableCollection<DeviceModuleState> Devices { get; }
        string ConfigurationPath { get; }

        Task LoadAsync();
        Task SaveAsync();
        Task CheckEquipmentAsync(CancellationToken cancellationToken = default);
        Task<DeviceOperationResult> ProbeDeviceAsync(DeviceModuleState device, CancellationToken cancellationToken = default);
        Task<DeviceOperationResult> ReadDeviceAsync(DeviceModuleState device, int? employeeId = null, CancellationToken cancellationToken = default);
    }
}
