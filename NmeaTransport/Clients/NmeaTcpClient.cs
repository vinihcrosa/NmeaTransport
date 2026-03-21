using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using NmeaTransport.Internal;

namespace NmeaTransport.Clients;

/// <summary>
/// Provides a reusable TCP client for sending and receiving NMEA sentences.
/// </summary>
public sealed class NmeaTcpClient : INmeaTcpClient
{
    private readonly Channel<QueuedMessage> _sendQueue = Channel.CreateUnbounded<QueuedMessage>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, NmeaMessageHandler>> _handlers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _stateLock = new();
    private readonly object _sessionLock = new();
    private readonly Encoding _encoding = Encoding.ASCII;
    private readonly string _host;
    private readonly int _port;
    private readonly NmeaTcpClientOptions _options;

    private TaskCompletionSource _connectionAvailable = CreateSignal();
    private CancellationTokenSource? _lifecycleCts;
    private Task? _lifecycleTask;
    private Task? _senderTask;
    private ClientSession? _activeSession;
    private int _lifecycleState;

    /// <summary>
    /// Creates a new instance of <see cref="NmeaTcpClient"/>.
    /// </summary>
    /// <param name="host">The remote server host name or IP address.</param>
    /// <param name="port">The remote TCP port.</param>
    /// <param name="options">Optional runtime configuration.</param>
    public NmeaTcpClient(string host, int port, NmeaTcpClientOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("The host must not be null or whitespace.", nameof(host));
        }

        if (port is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "The TCP port must be between 1 and 65535.");
        }

        _options = options ?? new NmeaTcpClientOptions();
        _options.Validate();

        _host = host;
        _port = port;
    }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        Task connectionTask;

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

            _lifecycleState = 1;
            _lifecycleCts = new CancellationTokenSource();
            var firstConnection = CreateSignal();
            _senderTask = ProcessOutgoingMessagesAsync(_lifecycleCts.Token);
            _lifecycleTask = RunLifecycleAsync(firstConnection, _lifecycleCts.Token);
            connectionTask = firstConnection.Task;
        }

        try
        {
            await connectionTask.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await DisconnectAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync()
    {
        CancellationTokenSource? lifecycleCts;
        Task[] tasksToAwait;

        lock (_stateLock)
        {
            if (_lifecycleState == 2)
            {
                return;
            }

            _lifecycleState = 2;
            lifecycleCts = _lifecycleCts;
            tasksToAwait = new[] { _lifecycleTask, _senderTask }
                .Where(task => task is not null)
                .Cast<Task>()
                .ToArray();
        }

        lifecycleCts?.Cancel();
        _sendQueue.Writer.TryComplete();

        var session = ClearActiveSession();
        session?.Dispose();

        if (tasksToAwait.Length == 0)
        {
            lifecycleCts?.Dispose();
            return;
        }

        try
        {
            await Task.WhenAll(tasksToAwait).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lifecycleCts?.Dispose();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return new ValueTask(DisconnectAsync());
    }

    /// <inheritdoc />
    public Task SendAsync(NmeaMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (Volatile.Read(ref _lifecycleState) == 2)
        {
            throw new InvalidOperationException("The client has already been disconnected.");
        }

        var sentence = NmeaSentence.Serialize(message);
        return _sendQueue.Writer.WriteAsync(new QueuedMessage(sentence), ct).AsTask();
    }

    /// <inheritdoc />
    public IDisposable RegisterHandler(string header, NmeaMessageHandler handler)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            throw new ArgumentException("The header must not be null or whitespace.", nameof(header));
        }

        ArgumentNullException.ThrowIfNull(handler);

        var registrations = _handlers.GetOrAdd(header, _ => new ConcurrentDictionary<Guid, NmeaMessageHandler>());
        var registrationId = Guid.NewGuid();
        registrations[registrationId] = handler;

        return new HandlerRegistration(this, header, registrationId);
    }

    private async Task RunLifecycleAsync(TaskCompletionSource firstConnection, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ClientSession? session = null;

                try
                {
                    session = await ConnectSessionAsync(ct).ConfigureAwait(false);
                    SetActiveSession(session);
                    firstConnection.TrySetResult();
                    Console.WriteLine($"Connected to {_host}:{_port}");

                    await ReadIncomingMessagesAsync(session, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"Client connection error: {exception.Message}");
                }
                finally
                {
                    if (session is not null)
                    {
                        InvalidateSession(session, "Disconnected from server.");
                    }
                }

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await Task.Delay(_options.ReconnectDelay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            firstConnection.TrySetCanceled(ct);
        }
    }

    private async Task ProcessOutgoingMessagesAsync(CancellationToken ct)
    {
        QueuedMessage? pendingMessage = null;

        while (!ct.IsCancellationRequested)
        {
            pendingMessage ??= await ReadNextQueuedMessageAsync(ct).ConfigureAwait(false);

            if (pendingMessage is null)
            {
                return;
            }

            var session = await WaitForActiveSessionAsync(ct).ConfigureAwait(false);

            try
            {
                await session.SendAsync($"{pendingMessage.Sentence}\r\n", _options.WriteTimeout, ct).ConfigureAwait(false);
                pendingMessage = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException or TimeoutException)
            {
                Console.WriteLine($"Client send error: {exception.Message}");
                InvalidateSession(session, "Disconnected while sending.");
            }
        }
    }

    private async Task<QueuedMessage?> ReadNextQueuedMessageAsync(CancellationToken ct)
    {
        try
        {
            return await _sendQueue.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    private async Task<ClientSession> ConnectSessionAsync(CancellationToken ct)
    {
        var tcpClient = new TcpClient();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_options.ConnectTimeout);
            await tcpClient.ConnectAsync(_host, _port, timeoutCts.Token).ConfigureAwait(false);
            return new ClientSession(tcpClient, _encoding);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tcpClient.Dispose();
            throw new TimeoutException($"Connection to {_host}:{_port} timed out after {_options.ConnectTimeout.TotalMilliseconds} ms.");
        }
        catch
        {
            tcpClient.Dispose();
            throw;
        }
    }

    private async Task ReadIncomingMessagesAsync(ClientSession session, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line;

            try
            {
                line = await session.Reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            if (!NmeaSentence.TryParse(line, _options.ValidateChecksum, out var message, out var error))
            {
                Console.WriteLine($"Invalid NMEA sentence discarded: {error} Raw='{line}'");
                continue;
            }

            Console.WriteLine($"RX: {line}");
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
                Console.WriteLine($"Handler error for header '{message.Header}': {exception.Message}");
            }
        }
    }

    private async Task<ClientSession> WaitForActiveSessionAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Task signalTask;

            lock (_sessionLock)
            {
                if (_activeSession is not null)
                {
                    return _activeSession;
                }

                signalTask = _connectionAvailable.Task;
            }

            await signalTask.WaitAsync(ct).ConfigureAwait(false);
        }

        throw new OperationCanceledException(ct);
    }

    private void SetActiveSession(ClientSession session)
    {
        lock (_sessionLock)
        {
            if (_connectionAvailable.Task.IsCompleted)
            {
                _connectionAvailable = CreateSignal();
            }

            _activeSession = session;
            _connectionAvailable.TrySetResult();
        }
    }

    private ClientSession? ClearActiveSession()
    {
        lock (_sessionLock)
        {
            var session = _activeSession;
            _activeSession = null;

            if (_connectionAvailable.Task.IsCompleted)
            {
                _connectionAvailable = CreateSignal();
            }

            return session;
        }
    }

    private void InvalidateSession(ClientSession session, string reason)
    {
        ClientSession? sessionToDispose = null;

        lock (_sessionLock)
        {
            if (ReferenceEquals(_activeSession, session))
            {
                _activeSession = null;
                sessionToDispose = session;

                if (_connectionAvailable.Task.IsCompleted)
                {
                    _connectionAvailable = CreateSignal();
                }
            }
        }

        if (sessionToDispose is not null)
        {
            Console.WriteLine(reason);
            sessionToDispose.Dispose();
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

    private static TaskCompletionSource CreateSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record QueuedMessage(string Sentence);

    private sealed class HandlerRegistration : IDisposable
    {
        private readonly NmeaTcpClient _owner;
        private readonly string _header;
        private readonly Guid _registrationId;
        private int _disposed;

        public HandlerRegistration(NmeaTcpClient owner, string header, Guid registrationId)
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

    private sealed class ClientSession : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly StreamWriter _writer;
        private bool _disposed;

        public ClientSession(TcpClient client, Encoding encoding)
        {
            Client = client;
            Stream = client.GetStream();
            Reader = new StreamReader(Stream, encoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            _writer = new StreamWriter(Stream, encoding, 1024, leaveOpen: true)
            {
                NewLine = "\r\n",
                AutoFlush = true
            };
        }

        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        public StreamReader Reader { get; }

        public async Task SendAsync(string sentence, TimeSpan timeout, CancellationToken ct)
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                ThrowIfDisposed();

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeout);

                try
                {
                    await _writer.WriteAsync(sentence.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
                    await _writer.FlushAsync().WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"Socket write timed out after {timeout.TotalMilliseconds} ms.");
                }
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
            _writer.Dispose();
            Reader.Dispose();
            Client.Dispose();
            _writeLock.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ClientSession));
            }
        }
    }
}
