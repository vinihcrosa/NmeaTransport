using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NmeaTransport.Clients;
using NmeaTransport.Internal;

namespace NmeaTransport.Server;

public sealed class NmeaTcpServer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, NmeaMessageHandler>> _handlers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateLock = new();
    private readonly ConcurrentDictionary<int, ClientConnection> _clients = new();
    private readonly List<Task> _clientTasks = new();
    private readonly TcpListener _listener;
    private readonly Encoding _encoding = Encoding.ASCII;
    private readonly int _port;

    private CancellationTokenSource? _lifecycleCts;
    private bool _isRunning;
    private int _nextClientId;

    /// <summary>
    /// Creates a new instance of <see cref="NmeaTcpServer"/>.
    /// </summary>
    /// <param name="port">The TCP port to listen on.</param>
    public NmeaTcpServer(int port)
    {
        _port = port;
        _listener = new TcpListener(IPAddress.Any, port);
    }

    /// <summary>
    /// Gets the server listening port.
    /// </summary>
    public int Port => _port;

    /// <summary>
    /// Starts accepting TCP clients until the server is stopped or the token is cancelled.
    /// </summary>
    /// <param name="ct">A token used to stop the server lifecycle.</param>
    public async Task StartAsync(CancellationToken ct = default)
    {
        CancellationTokenSource lifecycleCts;

        lock (_stateLock)
        {
            if (_isRunning)
            {
                throw new InvalidOperationException("The server is already running.");
            }

            _lifecycleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lifecycleCts = _lifecycleCts;
            _isRunning = true;
        }

        _listener.Start();
        Console.WriteLine($"NMEA TCP Server started on port {_port}...");
        var lifecycleToken = lifecycleCts.Token;

        try
        {
            while (!lifecycleToken.IsCancellationRequested)
            {
                TcpClient tcpClient;

                try
                {
                    tcpClient = await _listener.AcceptTcpClientAsync().WaitAsyncCompat(lifecycleToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifecycleToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (lifecycleToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException) when (lifecycleToken.IsCancellationRequested)
                {
                    break;
                }

                RegisterClient(tcpClient, lifecycleToken);
            }
        }
        finally
        {
            await ShutdownAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops the server and disconnects all connected clients.
    /// </summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? lifecycleCts;

        lock (_stateLock)
        {
            lifecycleCts = _lifecycleCts;

            if (!_isRunning || lifecycleCts is null)
            {
                return;
            }
        }

        lifecycleCts.Cancel();
        await ShutdownAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a structured NMEA message to all connected TCP clients.
    /// </summary>
    /// <param name="message">The message to broadcast.</param>
    /// <param name="ct">A token used to cancel the send operation.</param>
    public Task SendAsync(NmeaMessage message, CancellationToken ct = default)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        return BroadcastAsync(NmeaSentence.Serialize(message), ct);
    }

    /// <summary>
    /// Registers an asynchronous handler for a specific NMEA header.
    /// </summary>
    /// <param name="header">The NMEA header to route to.</param>
    /// <param name="handler">The handler to invoke for matching messages.</param>
    /// <returns>An <see cref="IDisposable"/> that unregisters the handler when disposed.</returns>
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

    internal static bool HasValidNmeaPrefix(string? sentence)
    {
        return NmeaSentence.HasValidPrefix(sentence);
    }

    internal static bool ValidateChecksum(string sentence)
    {
        return NmeaSentence.ValidateChecksum(sentence);
    }

    internal static bool IsValidSentence(string? sentence)
    {
        return NmeaSentence.TryParse(
            sentence,
            validateChecksum: sentence?.Contains('*') == true,
            out _,
            out _);
    }

    internal int ConnectedClientCount => _clients.Count;

    private void RegisterClient(TcpClient tcpClient, CancellationToken ct)
    {
        var clientId = Interlocked.Increment(ref _nextClientId);
        var connection = new ClientConnection(clientId, tcpClient, _encoding);

        if (!_clients.TryAdd(clientId, connection))
        {
            connection.Dispose();
            throw new InvalidOperationException("Failed to track connected client.");
        }

        Console.WriteLine($"Client connected ({clientId})");

        var task = HandleClientAsync(connection, ct);

        lock (_stateLock)
        {
            _clientTasks.Add(task);
        }

        _ = task.ContinueWith(
            completedTask => RemoveCompletedTask(connection, completedTask),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task HandleClientAsync(ClientConnection connection, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await connection.Reader.ReadLineAsync().ConfigureAwait(false);

                if (line is null)
                {
                    break;
                }

                if (!NmeaSentence.TryParse(
                    line,
                    validateChecksum: line.Contains('*'),
                    out var message,
                    out _))
                {
                    continue;
                }

                Console.WriteLine($"RX ({connection.Id}): {line}");
                await DispatchAsync(message!, ct).ConfigureAwait(false);
                await BroadcastAsync(line, ct).ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested)
        {
        }
        catch (IOException) when (ct.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Client error ({connection.Id}): {exception.Message}");
        }
        finally
        {
            RemoveClient(connection);
            Console.WriteLine($"Client disconnected ({connection.Id})");
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
                Console.WriteLine($"Handler error for header '{message.Header}': {exception.Message}");
            }
        }
    }

    private async Task BroadcastAsync(string message, CancellationToken ct)
    {
        var payload = _encoding.GetBytes(message + "\r\n");
        var snapshot = _clients.Values.ToArray();

        foreach (var client in snapshot)
        {
            try
            {
                await client.SendAsync(payload, ct).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                RemoveClient(client);
            }
            catch (IOException)
            {
                RemoveClient(client);
            }
            catch (SocketException)
            {
                RemoveClient(client);
            }
        }
    }

    private async Task ShutdownAsync()
    {
        List<Task> tasksToAwait;
        ClientConnection[] clientsToClose;
        CancellationTokenSource? lifecycleCts;
        var shouldShutdown = false;

        lock (_stateLock)
        {
            if (_isRunning)
            {
                _isRunning = false;
                shouldShutdown = true;
            }

            lifecycleCts = _lifecycleCts;
            _lifecycleCts = null;
            tasksToAwait = _clientTasks.ToList();
            clientsToClose = _clients.Values.ToArray();
        }

        if (!shouldShutdown && lifecycleCts is null)
        {
            return;
        }

        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
        }

        foreach (var client in clientsToClose)
        {
            RemoveClient(client);
        }

        try
        {
            await Task.WhenAll(tasksToAwait).ConfigureAwait(false);
        }
        catch
        {
            // Handler failures are already observed and logged individually.
        }
        finally
        {
            lifecycleCts?.Dispose();
        }
    }

    private void RemoveCompletedTask(ClientConnection connection, Task completedTask)
    {
        lock (_stateLock)
        {
            _clientTasks.Remove(completedTask);
        }

        if (completedTask.IsFaulted)
        {
            Console.WriteLine($"Client task faulted ({connection.Id}): {completedTask.Exception?.GetBaseException().Message}");
        }
    }

    private void RemoveClient(ClientConnection connection)
    {
        if (_clients.TryRemove(connection.Id, out _))
        {
            connection.Dispose();
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
        private readonly NmeaTcpServer _owner;
        private readonly string _header;
        private readonly Guid _registrationId;
        private int _disposed;

        public HandlerRegistration(NmeaTcpServer owner, string header, Guid registrationId)
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

    private sealed class ClientConnection : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly NetworkStream _stream;
        private bool _disposed;

        public ClientConnection(int id, TcpClient client, Encoding encoding)
        {
            Id = id;
            Client = client;
            _stream = client.GetStream();
            Reader = new StreamReader(_stream, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        }

        public int Id { get; }
        public TcpClient Client { get; }
        public StreamReader Reader { get; }

        public async Task SendAsync(byte[] payload, CancellationToken ct)
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                ThrowIfDisposed();
                await _stream.WriteAsync(payload, 0, payload.Length, ct).ConfigureAwait(false);
                await _stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Reader.Dispose();
            Client.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ClientConnection));
            }
        }
    }
}
