using System.Net;
using System.Net.Sockets;
using System.Text;
using NmeaTransport.Server;

namespace NmeaTransport.Test.TestSupport;

internal static class NmeaTestSentence
{
    public static string Create(string body)
    {
        var checksum = 0;

        foreach (var ch in body)
        {
            checksum ^= ch;
        }

        return $"${body}*{checksum:X2}";
    }
}

internal sealed class RestartableServerHarness : IAsyncDisposable
{
    private NmeaTcpServer? _server;
    private Task? _serverTask;

    public RestartableServerHarness()
    {
        Port = GetFreePort();
    }

    public int Port { get; }

    public async Task StartAsync()
    {
        if (_server is not null)
        {
            throw new InvalidOperationException("The server is already running.");
        }

        _server = new NmeaTcpServer(Port);
        _serverTask = _server.StartAsync();
        await WaitUntilListeningAsync().ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        if (_server is null)
        {
            return;
        }

        try
        {
            await _server.StopAsync().ConfigureAwait(false);

            if (_serverTask is not null)
            {
                await _serverTask.ConfigureAwait(false);
            }
        }
        finally
        {
            _server = null;
            _serverTask = null;
        }
    }

    public async Task<RawTcpClient> ConnectClientAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Port).ConfigureAwait(false);
        return new RawTcpClient(client);
    }

    public async Task WaitForConnectedClientCountAsync(int expectedCount, TimeSpan? timeout = null)
    {
        if (_server is null)
        {
            throw new InvalidOperationException("The server is not running.");
        }

        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));

        while (_server.ConnectedClientCount < expectedCount)
        {
            await Task.Delay(25, timeoutCts.Token).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task WaitUntilListeningAsync()
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (true)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, Port, timeoutCts.Token).ConfigureAwait(false);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(50, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Server did not start listening on port {Port} within the expected time.");
            }
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

internal sealed class RawTcpClient : IAsyncDisposable
{
    private readonly Encoding _encoding = Encoding.ASCII;
    private readonly NetworkStream _stream;
    private readonly StreamWriter _writer;
    private readonly TcpClient _client;

    public RawTcpClient(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
        _writer = new StreamWriter(_stream, _encoding, 1024, leaveOpen: true)
        {
            NewLine = "\r\n",
            AutoFlush = true
        };
    }

    public async Task SendLineAsync(string line)
    {
        await _writer.WriteLineAsync(line).ConfigureAwait(false);
    }

    public async Task<string> ReadRawLineAsync(TimeSpan? timeout = null)
    {
        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(3));
        var buffer = new List<byte>();
        var oneByte = new byte[1];

        while (true)
        {
            var read = await _stream.ReadAsync(oneByte, timeoutCts.Token).ConfigureAwait(false);

            if (read == 0)
            {
                throw new IOException("Connection closed before a line terminator was received.");
            }

            buffer.Add(oneByte[0]);

            if (buffer.Count >= 2 &&
                buffer[^2] == '\r' &&
                buffer[^1] == '\n')
            {
                return _encoding.GetString(buffer.ToArray());
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _writer.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class TestTcpPeerServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Task<TcpClient> _acceptTask;
    private TcpClient? _acceptedClient;
    private StreamWriter? _writer;

    public TestTcpPeerServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = _listener.AcceptTcpClientAsync();
    }

    public int Port { get; }

    public async Task WaitForClientAsync()
    {
        if (_acceptedClient is not null)
        {
            return;
        }

        _acceptedClient = await _acceptTask.ConfigureAwait(false);
        _writer = new StreamWriter(_acceptedClient.GetStream(), Encoding.ASCII, 1024, leaveOpen: true)
        {
            NewLine = "\r\n",
            AutoFlush = true
        };
    }

    public async Task SendLineAsync(string line)
    {
        await WaitForClientAsync().ConfigureAwait(false);
        await _writer!.WriteLineAsync(line).ConfigureAwait(false);
    }

    public async Task DisconnectClientAsync()
    {
        await WaitForClientAsync().ConfigureAwait(false);
        _writer?.Dispose();
        _acceptedClient?.Dispose();
        _writer = null;
        _acceptedClient = null;
    }

    public ValueTask DisposeAsync()
    {
        _writer?.Dispose();
        _acceptedClient?.Dispose();
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
