using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SmartWatchProj.Models.Devices;
using SmartWatchProj.Services.Logging;

namespace SmartWatchProj.Services.Devices
{
    public sealed class DeviceManager : IDeviceManager
    {
        private readonly DeviceConfigurationStore _configurationStore;
        private readonly IAppLogger _logger;

        public DeviceManager(DeviceConfigurationStore configurationStore, IAppLogger logger)
        {
            _configurationStore = configurationStore;
            _logger = logger;
        }

        public ObservableCollection<DeviceModuleState> Devices { get; } = new();

        public string ConfigurationPath => _configurationStore.ConfigurationPath;

        public async Task LoadAsync()
        {
            var devices = await _configurationStore.LoadAsync();

            Devices.Clear();
            foreach (var device in devices.OrderBy(item => item.SortOrder))
            {
                Devices.Add(device);
            }
        }

        public Task SaveAsync()
        {
            return _configurationStore.SaveAsync(Devices);
        }

        public async Task CheckEquipmentAsync(CancellationToken cancellationToken = default)
        {
            foreach (var device in Devices.OrderBy(item => item.SortOrder))
            {
                await ProbeDeviceAsync(device, cancellationToken);
            }
        }

        public Task<DeviceOperationResult> ProbeDeviceAsync(DeviceModuleState device, CancellationToken cancellationToken = default)
        {
            return ExecuteDeviceOperationAsync(device, null, cancellationToken);
        }

        public Task<DeviceOperationResult> ReadDeviceAsync(DeviceModuleState device, int? employeeId = null, CancellationToken cancellationToken = default)
        {
            return ExecuteDeviceOperationAsync(device, employeeId, cancellationToken);
        }

        private async Task<DeviceOperationResult> ExecuteDeviceOperationAsync(
            DeviceModuleState device,
            int? employeeId,
            CancellationToken cancellationToken)
        {
            if (!device.IsEnabled)
            {
                device.Status = DeviceStatus.NotConfigured;
                device.StatusMessage = "Отключено в настройках";

                return CreateFailure(device, DeviceStatus.NotConfigured, "Устройство отключено в настройках.");
            }

            if (device.ConnectionType != DeviceConnectionType.Serial)
            {
                device.Status = DeviceStatus.NotConfigured;
                device.StatusMessage = "Тип подключения не поддерживается";

                return CreateFailure(device, DeviceStatus.NotConfigured, "Сейчас поддерживается только Serial/COM.");
            }

            var candidatePorts = ResolveCandidatePorts(device).ToList();
            if (candidatePorts.Count == 0)
            {
                device.Status = device.PortMode == DevicePortMode.Manual
                    ? DeviceStatus.NotConfigured
                    : DeviceStatus.NotFound;
                device.StatusMessage = device.PortMode == DevicePortMode.Manual
                    ? "Ручной COM-порт не указан"
                    : "COM-порты не найдены";
                device.LastError = device.StatusMessage;
                device.LastCheckedAt = DateTimeOffset.Now;

                return CreateFailure(device, device.Status, device.StatusMessage);
            }

            Exception? lastException = null;

            foreach (var port in candidatePorts)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var options = CreateConnectionOptions(device, port);

                device.Status = DeviceStatus.Detecting;
                device.StatusMessage = $"Проверка {port}";
                device.ResolvedPortName = string.Empty;
                device.LastError = string.Empty;
                device.LastResponsePreview = string.Empty;

                _logger.Info($"[{device.DisplayName}] Попытка подключения к {port} ({device.BaudRate} бод).");

                await using var transport = CreateTransportWithLogging(device.DisplayName, port);
                await using var client = new Esp32DeviceClient(transport, options);

                try
                {
                    await client.ConnectAsync(cancellationToken);

                    device.Status = DeviceStatus.Connected;
                    device.StatusMessage = $"Подключено к {port}";
                    device.ResolvedPortName = port;

                    var result = await ExecuteProtocolAsync(device, client, employeeId, cancellationToken);
                    result.PortName = port;

                    device.Status = DeviceStatus.Ready;
                    device.StatusMessage = "Готово к работе";
                    device.ResolvedPortName = port;
                    device.LastCheckedAt = DateTimeOffset.Now;
                    device.LastError = string.Empty;
                    device.LastResponsePreview = result.JsonResponse ?? string.Empty;

                    _logger.Info($"[{device.DisplayName}] Успешный ответ от {port}.");

                    return result;
                }
                catch (TimeoutException timeoutException)
                {
                    lastException = timeoutException;
                    _logger.Warning($"[{device.DisplayName}] Таймаут при работе с {port}: {timeoutException.Message}");
                }
                catch (Exception exception)
                {
                    lastException = exception;
                    _logger.Error($"[{device.DisplayName}] Ошибка при работе с {port}.", exception);
                }
                finally
                {
                    await client.DisconnectAsync();
                }
            }

            var finalStatus = lastException is TimeoutException
                ? DeviceStatus.NotFound
                : DeviceStatus.Error;

            var message = lastException?.Message ?? "Устройство не ответило ни на одном COM-порту.";

            device.Status = finalStatus;
            device.StatusMessage = finalStatus == DeviceStatus.NotFound ? "Устройство не найдено" : "Ошибка подключения";
            device.LastError = message;
            device.LastCheckedAt = DateTimeOffset.Now;
            device.ResolvedPortName = string.Empty;

            return CreateFailure(device, finalStatus, message);
        }

        private IEnumerable<string> ResolveCandidatePorts(DeviceModuleState device)
        {
            if (device.PortMode == DevicePortMode.Manual)
            {
                if (!string.IsNullOrWhiteSpace(device.PortName))
                {
                    yield return device.PortName.Trim();
                }

                yield break;
            }

            foreach (var port in SerialPort.GetPortNames().OrderBy(port => port, StringComparer.OrdinalIgnoreCase))
            {
                yield return port;
            }
        }

        private static SerialPortConnectionOptions CreateConnectionOptions(DeviceModuleState device, string portName)
        {
            return new SerialPortConnectionOptions
            {
                PortName = portName,
                BaudRate = device.BaudRate > 0 ? device.BaudRate : 115200,
                ResponseTimeout = TimeSpan.FromMilliseconds(device.TimeoutMs > 0 ? device.TimeoutMs : 10000)
            };
        }

        private ISerialDeviceTransport CreateTransportWithLogging(string deviceName, string portName)
        {
            var transport = new SerialPortDeviceTransport();
            transport.RawChunkReceived += (_, chunk) =>
            {
                _logger.Info($"[{deviceName}] RX chunk {portName}: {EscapeForLog(chunk)}");
            };
            transport.JsonMessageReceived += (_, json) =>
            {
                _logger.Info($"[{deviceName}] RX json {portName}: {json}");
            };

            return transport;
        }

        private static string EscapeForLog(string chunk)
        {
            return chunk
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);
        }

        private async Task<DeviceOperationResult> ExecuteProtocolAsync(
            DeviceModuleState device,
            Esp32DeviceClient client,
            int? employeeId,
            CancellationToken cancellationToken)
        {
            return device.ModuleType switch
            {
                DeviceModuleType.Thermometer => await ExecutePrimaryStationReadAsync(
                    device,
                    () => client.ReadTemperatureAsync(cancellationToken)),

                DeviceModuleType.AlcoholTester => await ExecutePrimaryStationReadAsync(
                    device,
                    () => client.ReadAlcoholAsync(cancellationToken)),

                DeviceModuleType.BloodPressureMonitor => await ExecutePrimaryStationReadAsync(
                    device,
                    () => client.ReadPressureAsync(cancellationToken)),

                DeviceModuleType.WearableMonitor => await ExecuteWearableReadAsync(
                    device,
                    client,
                    employeeId,
                    cancellationToken),

                _ => CreateFailure(device, DeviceStatus.Error, "Неизвестный тип устройства.")
            };
        }

        private static async Task<DeviceOperationResult> ExecutePrimaryStationReadAsync(
            DeviceModuleState device,
            Func<Task<PrimaryStationResponse>> operation)
        {
            var response = await operation();
            var json = JsonSerializer.Serialize(response);

            return new DeviceOperationResult
            {
                DeviceId = device.DeviceId,
                DeviceName = device.DisplayName,
                ModuleType = device.ModuleType,
                Success = true,
                Status = DeviceStatus.Ready,
                Message = "Данные получены.",
                JsonResponse = json,
                PrimaryStationResponse = response
            };
        }

        private static async Task<DeviceOperationResult> ExecuteWearableReadAsync(
            DeviceModuleState device,
            Esp32DeviceClient client,
            int? employeeId,
            CancellationToken cancellationToken)
        {
            if (employeeId.HasValue && employeeId.Value > 0)
            {
                await client.SendWearableUserIdAsync(employeeId.Value, cancellationToken);
                await Task.Delay(250, cancellationToken);
            }

            var response = await client.ReadWearableDataAsync(cancellationToken);
            var json = JsonSerializer.Serialize(response);

            return new DeviceOperationResult
            {
                DeviceId = device.DeviceId,
                DeviceName = device.DisplayName,
                ModuleType = device.ModuleType,
                Success = true,
                Status = DeviceStatus.Ready,
                Message = "Данные носимого устройства получены.",
                JsonResponse = json,
                WearableDeviceResponse = response
            };
        }

        private static DeviceOperationResult CreateFailure(DeviceModuleState device, DeviceStatus status, string message)
        {
            return new DeviceOperationResult
            {
                DeviceId = device.DeviceId,
                DeviceName = device.DisplayName,
                ModuleType = device.ModuleType,
                Success = false,
                Status = status,
                Message = message
            };
        }
    }
}
