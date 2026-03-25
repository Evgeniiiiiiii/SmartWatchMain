using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SmartWatchProj.Services.Devices
{
    public interface ISerialDeviceTransport : IAsyncDisposable
    {
        event EventHandler<string>? RawChunkReceived;
        event EventHandler<string>? JsonMessageReceived;

        bool IsOpen { get; }
        string? PortName { get; }

        IReadOnlyList<string> GetAvailablePorts();
        Task OpenAsync(SerialPortConnectionOptions options, CancellationToken cancellationToken = default);
        Task CloseAsync();
        Task SendAsync(string payload, bool appendNewLine = false, CancellationToken cancellationToken = default);
        Task<string> ReadNextJsonAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
        Task<string> SendAndReceiveJsonAsync(string payload, TimeSpan timeout, bool appendNewLine = false, CancellationToken cancellationToken = default);
        Task<T> SendAndReceiveJsonAsync<T>(string payload, TimeSpan timeout, bool appendNewLine = false, CancellationToken cancellationToken = default);
    }
}
