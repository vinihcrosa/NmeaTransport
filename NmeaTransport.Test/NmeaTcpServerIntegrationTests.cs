using System.Net;
using System.Net.Sockets;
using System.Text;
using NmeaTransport.Server;

namespace NmeaTransport.Test;

public class NmeaTcpServerIntegrationTests
{
    [Fact]
    public async Task BroadcastAsync_ForwardsValidSentenceToConnectedClients()
    {
        await using var harness = await ServerHarness.StartAsync();
        await using var sender = await harness.ConnectClientAsync();
        await using var receiver = await harness.ConnectClientAsync();

        const string sentence = "$GPGLL,4916.45,N,12311.12,W,225444,A,*1D";

        await sender.SendLineAsync(sentence);

        var received = await receiver.ReadRawLineAsync();

        Assert.Equal($"{sentence}\r\n", received);
    }

    [Fact]
    public async Task BroadcastAsync_IgnoresDisconnectedClientAndKeepsServingOthers()
    {
        await using var harness = await ServerHarness.StartAsync();
        await using var sender = await harness.ConnectClientAsync();
        await using var receiver = await harness.ConnectClientAsync();
        var disconnectingClient = await harness.ConnectClientAsync();

        await disconnectingClient.DisposeAsync();
        await Task.Delay(100);

        const string sentence = "$GPGLL,4916.45,N,12311.12,W,225444,A,*1D";

        await sender.SendLineAsync(sentence);
        var received = await receiver.ReadRawLineAsync();

        Assert.Equal($"{sentence}\r\n", received);
        Assert.False(harness.ServerTask.IsFaulted);
    }

    [Fact]
    public async Task BroadcastAsync_DoesNotInterleaveConcurrentWrites()
    {
        await using var harness = await ServerHarness.StartAsync();
        await using var senderOne = await harness.ConnectClientAsync();
        await using var senderTwo = await harness.ConnectClientAsync();
        await using var receiver = await harness.ConnectClientAsync();

        var messagesFromOne = Enumerable.Range(0, 10).Select(index => CreateSentence($"SRC1,{index}")).ToArray();
        var messagesFromTwo = Enumerable.Range(0, 10).Select(index => CreateSentence($"SRC2,{index}")).ToArray();

        var sendOneTask = Task.Run(async () =>
        {
            foreach (var message in messagesFromOne)
            {
                await senderOne.SendLineAsync(message);
            }
        });

        var sendTwoTask = Task.Run(async () =>
        {
            foreach (var message in messagesFromTwo)
            {
                await senderTwo.SendLineAsync(message);
            }
        });

        await Task.WhenAll(sendOneTask, sendTwoTask);

        var received = new List<string>();

        for (var index = 0; index < messagesFromOne.Length + messagesFromTwo.Length; index++)
        {
            received.Add(await receiver.ReadRawLineAsync());
        }

        var expected = messagesFromOne
            .Concat(messagesFromTwo)
            .Select(message => $"{message}\r\n")
            .OrderBy(message => message)
            .ToArray();

        var actual = received.OrderBy(message => message).ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task StopAsync_ShutsDownServerDeterministically()
    {
        await using var harness = await ServerHarness.StartAsync();
        await using var client = await harness.ConnectClientAsync();

        await harness.Server.StopAsync();

        var completedTask = await Task.WhenAny(harness.ServerTask, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(harness.ServerTask, completedTask);
        await harness.ServerTask;
    }

    private static string CreateSentence(string body)
    {
        var checksum = 0;

        foreach (var ch in body)
        {
            checksum ^= ch;
        }

        return $"${body}*{checksum:X2}";
    }

    private sealed class ServerHarness : IAsyncDisposable
    {
        private ServerHarness(NmeaTcpServer server, Task serverTask)
        {
            Server = server;
            ServerTask = serverTask;
        }

        public NmeaTcpServer Server { get; }
        public Task ServerTask { get; }

        public static async Task<ServerHarness> StartAsync()
        {
            var port = GetFreePort();
            var server = new NmeaTcpServer(port);
            var harness = new ServerHarness(server, server.StartAsync());

            await harness.WaitUntilListeningAsync();
            return harness;
        }

        public async Task<TestClient> ConnectClientAsync()
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, Server.Port);
            await WaitForClientCountAsync(_testClientsCreated + 1);
            _testClientsCreated++;
            return new TestClient(client);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Server.StopAsync();
                await ServerTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task WaitUntilListeningAsync()
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            while (true)
            {
                try
                {
                    using var probe = new TcpClient();
                    await probe.ConnectAsync(IPAddress.Loopback, Server.Port, timeoutCts.Token);
                    return;
                }
                catch (SocketException)
                {
                    await Task.Delay(50, timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException($"Server did not start listening on port {Server.Port} within the expected time.");
                }
            }
        }

        private int _testClientsCreated;

        private async Task WaitForClientCountAsync(int expectedCount)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            while (Server.ConnectedClientCount < expectedCount)
            {
                await Task.Delay(25, timeoutCts.Token);
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

    private sealed class TestClient : IAsyncDisposable
    {
        private readonly Encoding _encoding = Encoding.ASCII;
        private readonly NetworkStream _stream;
        private readonly StreamWriter _writer;
        private readonly TcpClient _client;

        public TestClient(TcpClient client)
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
            await _writer.WriteLineAsync(line);
        }

        public async Task<string> ReadRawLineAsync()
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var buffer = new List<byte>();
            var oneByte = new byte[1];

            while (true)
            {
                var read = await _stream.ReadAsync(oneByte, timeoutCts.Token);

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
}
