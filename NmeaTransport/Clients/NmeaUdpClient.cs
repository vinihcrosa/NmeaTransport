using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NmeaTransport.Internal;

namespace NmeaTransport.Clients;

/// <summary>
/// Provides a reusable UDP client for sending and receiving NMEA sentences.
/// </summary>
public sealed class NmeaUdpClient : INmeaUdpClient
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, NmeaMessageHandler>> _handlers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _stateLock = new();
    private readonly object _clientLock = new();
    private readonly Encoding _encoding = Encoding.ASCII;
    private readonly int _port;
    private readonly NmeaUdpClientOptions _options;

    private CancellationTokenSource? _lifecycleCts;
    private Task? _receiverTask;
    private UdpClient? _activeClient;
    private int _lifecycleState;

    /// <summary>
    /// Creates a new instance of <see cref="NmeaUdpClient"/>.
    /// </summary>
    /// <param name="port">The local UDP port used for listening and broadcast sends.</param>
    /// <param name="options">Optional runtime configuration.</param>
    public NmeaUdpClient(int port, NmeaUdpClientOptions? options = null)
    {
        if (port is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "The UDP port must be between 1 and 65535.");
        }

        _options = options ?? new NmeaUdpClientOptions();
        _options.Validate();

        _port = port;
    }

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        lock (_stateLock)
        {
            if (_lifecycleState == 1)
            {
                throw new InvalidOperationException("The client has already been started.");
            }

            if (_lifecycleState == 2)
            {
                throw new InvalidOperationException("The client has already been disconnected and cannot be restarted.");
            }

            var udpClient = CreateUdpClient();
            _lifecycleState = 1;
            _lifecycleCts = new CancellationTokenSource();
            _receiverTask = Task.Run(() => ReceiveLoopAsync(udpClient, _lifecycleCts.Token), CancellationToken.None);

            lock (_clientLock)
            {
                _activeClient = udpClient;
            }
        }

        Log($"Listening on UDP port {_port}");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DisconnectAsync()
    {
        CancellationTokenSource? lifecycleCts;
        Task? receiverTask;
        UdpClient? clientToDispose;

        lock (_stateLock)
        {
            if (_lifecycleState == 2)
            {
                return;
            }

            _lifecycleState = 2;
            lifecycleCts = _lifecycleCts;
            receiverTask = _receiverTask;

            lock (_clientLock)
            {
                clientToDispose = _activeClient;
                _activeClient = null;
            }
        }

        lifecycleCts?.Cancel();
        clientToDispose?.Dispose();

        if (receiverTask is not null)
        {
            try
            {
                await receiverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        lifecycleCts?.Dispose();
        Log($"Stopped listening on UDP port {_port}");
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return new ValueTask(DisconnectAsync());
    }

    /// <inheritdoc />
    public Task SendAsync(NmeaMessage message, CancellationToken ct = default)
    {
        if (_options.DefaultRemoteEndPoint is null)
        {
            throw new InvalidOperationException("A default remote endpoint is not configured for this UDP client.");
        }

        return SendAsync(message, _options.DefaultRemoteEndPoint, ct);
    }

    /// <inheritdoc />
    public Task SendAsync(NmeaMessage message, IPEndPoint remoteEndPoint, CancellationToken ct = default)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        if (remoteEndPoint is null)
        {
            throw new ArgumentNullException(nameof(remoteEndPoint));
        }

        ct.ThrowIfCancellationRequested();

        if (Volatile.Read(ref _lifecycleState) == 2)
        {
            throw new InvalidOperationException("The client has already been disconnected.");
        }

        if (Volatile.Read(ref _lifecycleState) == 0)
        {
            throw new InvalidOperationException("The client has not been started. Call ConnectAsync first.");
        }

        SendCore(message, remoteEndPoint);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SendBroadcastAsync(NmeaMessage message, CancellationToken ct = default)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        ct.ThrowIfCancellationRequested();

        if (Volatile.Read(ref _lifecycleState) == 2)
        {
            throw new InvalidOperationException("The client has already been disconnected.");
        }

        if (Volatile.Read(ref _lifecycleState) == 0)
        {
            throw new InvalidOperationException("The client has not been started. Call ConnectAsync first.");
        }

        var broadcastEndPoint = new IPEndPoint(_options.BroadcastAddress, _port);
        SendCore(message, broadcastEndPoint);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IDisposable RegisterHandler(string header, NmeaMessageHandler handler)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            throw new ArgumentException("The header must not be null or whitespace.", nameof(header));
        }

        if (handler is null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        var registrations = _handlers.GetOrAdd(header, _ => new ConcurrentDictionary<Guid, NmeaMessageHandler>());
        var registrationId = Guid.NewGuid();
        registrations[registrationId] = handler;

        return new HandlerRegistration(this, header, registrationId);
    }

    private async Task ReceiveLoopAsync(UdpClient client, CancellationToken ct)
    {
        var remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested)
        {
            byte[]? buffer = null;

            try
            {
                buffer = client.Receive(ref remoteEndPoint);
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested || Volatile.Read(ref _lifecycleState) == 2)
            {
                break;
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.TimedOut)
            {
                continue;
            }
            catch (SocketException exception) when (ct.IsCancellationRequested || Volatile.Read(ref _lifecycleState) == 2)
            {
                if (exception.SocketErrorCode is SocketError.Interrupted or SocketError.OperationAborted)
                {
                    break;
                }

                break;
            }
            catch (Exception exception)
            {
                Log($"UDP receive error: {exception.Message}");
                continue;
            }

            if (buffer.Length == 0)
            {
                continue;
            }

            var rawSentence = _encoding.GetString(buffer).TrimEnd('\r', '\n');

            if (!NmeaSentence.TryParse(rawSentence, _options.ValidateChecksum, out var message, out var error))
            {
                Log($"Invalid NMEA sentence discarded: {error} Raw='{rawSentence}'");
                continue;
            }

            Log($"RX: {rawSentence}");
            await DispatchAsync(message!, ct).ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(NmeaMessage message, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(message.Header, out var registrations) || registrations.IsEmpty)
        {
            return;
        }

        foreach (var handler in registrations.Values)
        {
            try
            {
                await handler(message, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Log($"Handler error for header '{message.Header}': {exception.Message}");
            }
        }
    }

    private void SendCore(NmeaMessage message, IPEndPoint remoteEndPoint)
    {
        var sentence = NmeaSentence.Serialize(message);
        var payload = _encoding.GetBytes(sentence);
        var client = GetActiveClient();

        client.Send(payload, payload.Length, remoteEndPoint);
        Log($"TX: {sentence} -> {remoteEndPoint.Address}:{remoteEndPoint.Port}");
    }

    private UdpClient GetActiveClient()
    {
        lock (_clientLock)
        {
            if (_activeClient is null)
            {
                throw new InvalidOperationException("The client is not connected.");
            }

            return _activeClient;
        }
    }

    private UdpClient CreateUdpClient()
    {
        var client = new UdpClient(AddressFamily.InterNetwork);

        try
        {
            client.Client.ExclusiveAddressUse = false;
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.EnableBroadcast = true;
            client.Client.ReceiveTimeout = (int)_options.ReceiveTimeout.TotalMilliseconds;
            client.Client.SendTimeout = (int)_options.WriteTimeout.TotalMilliseconds;
            client.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private void Log(string message)
    {
        if (_options.EnableLogging == true)
        {
            Console.WriteLine(message);
        }
    }

    private void RemoveHandler(string header, Guid registrationId)
    {
        if (!_handlers.TryGetValue(header, out var registrations))
        {
            return;
        }

        registrations.TryRemove(registrationId, out _);

        if (registrations.IsEmpty)
        {
            _handlers.TryRemove(header, out _);
        }
    }

    private sealed class HandlerRegistration : IDisposable
    {
        private readonly NmeaUdpClient _owner;
        private readonly string _header;
        private readonly Guid _registrationId;
        private int _disposed;

        public HandlerRegistration(NmeaUdpClient owner, string header, Guid registrationId)
        {
            _owner = owner;
            _header = header;
            _registrationId = registrationId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _owner.RemoveHandler(_header, _registrationId);
        }
    }
}
