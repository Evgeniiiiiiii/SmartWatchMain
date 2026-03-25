using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SmartWatchProj.Services.Devices
{
    public sealed class SerialPortDeviceTransport : ISerialDeviceTransport
    {
        private readonly SemaphoreSlim _requestLock = new(1, 1);
        private readonly Channel<string> _jsonMessages = Channel.CreateUnbounded<string>();
        private readonly JsonMessageBuffer _messageBuffer = new();
        private readonly object _bufferLock = new();

        private SerialPort? _serialPort;
        private CancellationTokenSource? _readLoopCts;
        private Task? _readLoopTask;

        public event EventHandler<string>? RawChunkReceived;
        public event EventHandler<string>? JsonMessageReceived;

        public bool IsOpen => _serialPort?.IsOpen == true;

        public string? PortName => _serialPort?.PortName;

        public IReadOnlyList<string> GetAvailablePorts()
        {
            return SerialPort.GetPortNames()
                .OrderBy(port => port, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public Task OpenAsync(SerialPortConnectionOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(options.PortName))
            {
                throw new ArgumentException("Port name is required.", nameof(options));
            }

            if (IsOpen)
            {
                if (string.Equals(_serialPort?.PortName, options.PortName, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.CompletedTask;
                }

                throw new InvalidOperationException("Another serial port is already open.");
            }

            var serialPort = new SerialPort(options.PortName, options.BaudRate, options.Parity, options.DataBits, options.StopBits)
            {
                Handshake = options.Handshake,
                DtrEnable = options.DtrEnable,
                RtsEnable = options.RtsEnable,
                Encoding = options.Encoding,
                NewLine = options.NewLine,
                ReadTimeout = SerialPort.InfiniteTimeout,
                WriteTimeout = (int)Math.Max(1, options.WriteTimeout.TotalMilliseconds)
            };

            serialPort.Open();

            _serialPort = serialPort;
            ResetInboundState();
            _readLoopCts = new CancellationTokenSource();
            _readLoopTask = Task.Run(() => ReadLoopAsync(serialPort, _readLoopCts.Token), _readLoopCts.Token);

            return Task.CompletedTask;
        }

        public async Task CloseAsync()
        {
            var readLoopCts = _readLoopCts;
            var readLoopTask = _readLoopTask;
            var serialPort = _serialPort;

            _readLoopCts = null;
            _readLoopTask = null;
            _serialPort = null;

            if (readLoopCts != null)
            {
                try
                {
                    readLoopCts.Cancel();
                }
                catch
                {
                }
            }

            if (serialPort != null)
            {
                try
                {
                    if (serialPort.IsOpen)
                    {
                        serialPort.Close();
                    }
                }
                catch
                {
                }

                serialPort.Dispose();
            }

            if (readLoopTask != null)
            {
                try
                {
                    await readLoopTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                catch (IOException)
                {
                }
            }

            readLoopCts?.Dispose();
            DrainPendingMessages();
        }

        public async Task SendAsync(string payload, bool appendNewLine = false, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(payload))
            {
                throw new ArgumentException("Payload is required.", nameof(payload));
            }

            await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await WriteInternalAsync(payload, appendNewLine, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _requestLock.Release();
            }
        }

        public async Task<string> ReadNextJsonAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            EnsureOpen();

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                return await _jsonMessages.Reader.ReadAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:F1}s while waiting for a JSON response from {PortName}.");
            }
        }

        public async Task<string> SendAndReceiveJsonAsync(string payload, TimeSpan timeout, bool appendNewLine = false, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(payload))
            {
                throw new ArgumentException("Payload is required.", nameof(payload));
            }

            await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ResetInboundState();
                await WriteInternalAsync(payload, appendNewLine, cancellationToken).ConfigureAwait(false);
                return await ReadNextJsonAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _requestLock.Release();
            }
        }

        public async Task<T> SendAndReceiveJsonAsync<T>(string payload, TimeSpan timeout, bool appendNewLine = false, CancellationToken cancellationToken = default)
        {
            var json = await SendAndReceiveJsonAsync(payload, timeout, appendNewLine, cancellationToken).ConfigureAwait(false);
            var model = JsonSerializer.Deserialize<T>(json);

            if (model is null)
            {
                throw new JsonException($"Unable to deserialize device response to {typeof(T).Name}. JSON: {json}");
            }

            return model;
        }

        public async ValueTask DisposeAsync()
        {
            await CloseAsync().ConfigureAwait(false);
            _requestLock.Dispose();
        }

        private async Task WriteInternalAsync(string payload, bool appendNewLine, CancellationToken cancellationToken)
        {
            EnsureOpen();

            var serialPort = _serialPort!;
            var buffer = serialPort.Encoding.GetBytes(appendNewLine ? payload + serialPort.NewLine : payload);

            await serialPort.BaseStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            await serialPort.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task ReadLoopAsync(SerialPort serialPort, CancellationToken cancellationToken)
        {
            var buffer = new byte[512];

            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead;

                try
                {
                    bytesRead = await serialPort.BaseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }

                if (bytesRead <= 0)
                {
                    continue;
                }

                var chunk = serialPort.Encoding.GetString(buffer, 0, bytesRead);
                RawChunkReceived?.Invoke(this, chunk);

                lock (_bufferLock)
                {
                    _messageBuffer.Append(chunk);

                    while (_messageBuffer.TryReadNext(out var json) && !string.IsNullOrWhiteSpace(json))
                    {
                        _jsonMessages.Writer.TryWrite(json);
                        JsonMessageReceived?.Invoke(this, json);
                    }
                }
            }
        }

        private void EnsureOpen()
        {
            if (!IsOpen)
            {
                throw new InvalidOperationException("Serial port is not open.");
            }
        }

        private void DrainPendingMessages()
        {
            while (_jsonMessages.Reader.TryRead(out _))
            {
            }
        }

        private void ResetInboundState()
        {
            lock (_bufferLock)
            {
                _messageBuffer.Reset();
            }

            DrainPendingMessages();
        }
    }
}
