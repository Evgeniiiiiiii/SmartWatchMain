using System;
using System.Threading;
using System.Threading.Tasks;
using SmartWatchProj.Models.Devices;

namespace SmartWatchProj.Services.Devices
{
    public sealed class Esp32DeviceClient : IAsyncDisposable
    {
        private readonly ISerialDeviceTransport _transport;
        private readonly SerialPortConnectionOptions _options;

        public Esp32DeviceClient(ISerialDeviceTransport transport, SerialPortConnectionOptions options)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public bool IsConnected => _transport.IsOpen;

        public string? PortName => _transport.PortName;

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            return _transport.OpenAsync(_options, cancellationToken);
        }

        public Task DisconnectAsync()
        {
            return _transport.CloseAsync();
        }

        public Task<PrimaryStationResponse> ReadTemperatureAsync(CancellationToken cancellationToken = default)
        {
            return _transport.SendAndReceiveJsonAsync<PrimaryStationResponse>(Esp32Commands.ReadTemperature, _options.ResponseTimeout, cancellationToken: cancellationToken);
        }

        public Task<PrimaryStationResponse> ReadAlcoholAsync(CancellationToken cancellationToken = default)
        {
            return _transport.SendAndReceiveJsonAsync<PrimaryStationResponse>(Esp32Commands.ReadAlcohol, _options.ResponseTimeout, cancellationToken: cancellationToken);
        }

        public Task<PrimaryStationResponse> ReadPressureAsync(CancellationToken cancellationToken = default)
        {
            return _transport.SendAndReceiveJsonAsync<PrimaryStationResponse>(Esp32Commands.ReadPressure, _options.ResponseTimeout, cancellationToken: cancellationToken);
        }

        public Task<WearableDeviceResponse> ReadWearableDataAsync(CancellationToken cancellationToken = default)
        {
            return _transport.SendAndReceiveJsonAsync<WearableDeviceResponse>(Esp32Commands.ReadWearableData, _options.ResponseTimeout, cancellationToken: cancellationToken);
        }

        public Task SendWearableUserIdAsync(int userId, CancellationToken cancellationToken = default)
        {
            return _transport.SendAsync(Esp32Commands.BindWearableUser(userId), cancellationToken: cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return _transport.DisposeAsync();
        }
    }
}
