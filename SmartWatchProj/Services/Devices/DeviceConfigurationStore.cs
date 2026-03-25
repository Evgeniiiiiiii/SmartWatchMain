using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SmartWatchProj.Models.Devices;

namespace SmartWatchProj.Services.Devices
{
    public sealed class DeviceConfigurationStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public DeviceConfigurationStore(string? configurationPath = null)
        {
            ConfigurationPath = configurationPath
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "device-settings.json");
        }

        public string ConfigurationPath { get; }

        public async Task<List<DeviceModuleState>> LoadAsync()
        {
            if (!File.Exists(ConfigurationPath))
            {
                var defaults = CreateDefaultDevices();
                await SaveAsync(defaults).ConfigureAwait(false);
                return defaults;
            }

            await using var stream = File.OpenRead(ConfigurationPath);
            var configuration = await JsonSerializer.DeserializeAsync<DeviceConfigurationFile>(stream, SerializerOptions).ConfigureAwait(false);

            return configuration?.Devices?.OrderBy(device => device.SortOrder).ToList()
                ?? CreateDefaultDevices();
        }

        public async Task SaveAsync(IEnumerable<DeviceModuleState> devices)
        {
            var configuration = new DeviceConfigurationFile
            {
                Devices = devices.OrderBy(device => device.SortOrder).ToList()
            };

            await using var stream = File.Create(ConfigurationPath);
            await JsonSerializer.SerializeAsync(stream, configuration, SerializerOptions).ConfigureAwait(false);
        }

        private static List<DeviceModuleState> CreateDefaultDevices()
        {
            return new List<DeviceModuleState>
            {
                new()
                {
                    SortOrder = 1,
                    DeviceId = "temperature",
                    DisplayName = "Термометр",
                    ModuleType = DeviceModuleType.Thermometer,
                    ConnectionType = DeviceConnectionType.Serial,
                    PortMode = DevicePortMode.Auto,
                    PortName = string.Empty,
                    BaudRate = 115200,
                    TimeoutMs = 12000,
                    IsEnabled = true,
                    Status = DeviceStatus.NotConfigured,
                    StatusMessage = "Ожидает проверки"
                },
                new()
                {
                    SortOrder = 2,
                    DeviceId = "alcohol",
                    DisplayName = "Алкотестер",
                    ModuleType = DeviceModuleType.AlcoholTester,
                    ConnectionType = DeviceConnectionType.Serial,
                    PortMode = DevicePortMode.Auto,
                    PortName = string.Empty,
                    BaudRate = 115200,
                    TimeoutMs = 12000,
                    IsEnabled = true,
                    Status = DeviceStatus.NotConfigured,
                    StatusMessage = "Ожидает проверки"
                },
                new()
                {
                    SortOrder = 3,
                    DeviceId = "pressure",
                    DisplayName = "Тонометр",
                    ModuleType = DeviceModuleType.BloodPressureMonitor,
                    ConnectionType = DeviceConnectionType.Serial,
                    PortMode = DevicePortMode.Auto,
                    PortName = string.Empty,
                    BaudRate = 115200,
                    TimeoutMs = 15000,
                    IsEnabled = true,
                    Status = DeviceStatus.NotConfigured,
                    StatusMessage = "Ожидает проверки"
                },
                new()
                {
                    SortOrder = 4,
                    DeviceId = "wearable",
                    DisplayName = "Носимое устройство",
                    ModuleType = DeviceModuleType.WearableMonitor,
                    ConnectionType = DeviceConnectionType.Serial,
                    PortMode = DevicePortMode.Auto,
                    PortName = string.Empty,
                    BaudRate = 115200,
                    TimeoutMs = 15000,
                    IsEnabled = false,
                    Status = DeviceStatus.NotConfigured,
                    StatusMessage = "Отключено по умолчанию"
                }
            };
        }
    }
}
