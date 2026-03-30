using System.Net;
using System.Net.Sockets;
using System.Text;
using NmeaTransport.Clients;
using NmeaTransport.Server;

namespace NmeaTransport.Test;

public class NmeaTcpServerIntegrationTests
{
    [Fact]
    public async Task Logging_WithoutOptions_DoesNotWriteToConsole()
    {
        var output = await CaptureConsoleAsync(async () =>
        {
            await using var harness = await ServerHarness.StartAsync();
            await using var sender = await harness.ConnectClientAsync();

            await sender.SendLineAsync("$GPGLL,4916.45,N,12311.12,W,225444,A,*1D");
            await WaitForConsoleFlushAsync();
        });

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public async Task Logging_WhenSetToNull_DoesNotWriteToConsole()
    {
        var output = await CaptureConsoleAsync(async () =>
        {
            await using var harness = await ServerHarness.StartAsync(new NmeaTcpServerOptions
            {
                EnableLogging = null
            });
            await using var sender = await harness.ConnectClientAsync();

            await sender.SendLineAsync("$GPGLL,4916.45,N,12311.12,W,225444,A,*1D");
            await WaitForConsoleFlushAsync();
        });

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public async Task Logging_WhenEnabled_WritesStartConnectionReceiveAndDisconnectMessages()
    {
        var output = await CaptureConsoleAsync(async () =>
        {
            await using var harness = await ServerHarness.StartAsync(new NmeaTcpServerOptions
            {
                EnableLogging = true
            });
            await using var sender = await harness.ConnectClientAsync();

            await sender.SendLineAsync("$GPGLL,4916.45,N,12311.12,W,225444,A,*1D");
            await WaitForConsoleFlushAsync();
        });

        Assert.Contains($"NMEA TCP Server started on port", output);
        Assert.Contains("Client connected (", output);
        Assert.Contains("RX (", output);
        Assert.Contains("Client disconnected (", output);
    }

    [Fact]
    public async Task Logging_WhenEnabled_WritesHandlerErrorsToConsole()
    {
        var output = await CaptureConsoleAsync(async () =>
        {
            await using var harness = await ServerHarness.StartAsync(new NmeaTcpServerOptions
            {
                EnableLogging = true
            });
            await using var sender = await harness.ConnectClientAsync();

            using var registration = harness.Server.RegisterHandler("GPGLL", (_, _) =>
            {
                throw new InvalidOperationException("boom");
            });

            await sender.SendLineAsync("$GPGLL,4916.45,N,12311.12,W,225444,A,*1D");
            await WaitForConsoleFlushAsync();
        });

        Assert.Contains("Handler error for header 'GPGLL': boom", output);
    }

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
    public async Task RegisterHandlerAsync_DispatchesStructuredMessageAndPreservesBroadcast()
    {
        await using var harness = await ServerHarness.StartAsync();
        await using var sender = await harness.ConnectClientAsync();
        await using var receiver = await harness.ConnectClientAsync();
        var messageReceived = new TaskCompletionSource<NmeaMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = harness.Server.RegisterHandler("GPGLL", (message, _) =>
        {
            messageReceived.TrySetResult(message);
            return Task.CompletedTask;
        });

        const string sentence = "$GPGLL,4916.45,N,12311.12,W,225444,A,*1D";

        await sender.SendLineAsync(sentence);

        var dispatched = await messageReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var broadcast = await receiver.ReadRawLineAsync();

        Assert.Equal("GPGLL", dispatched.Header);
        Assert.Equal(["4916.45", "N", "12311.12", "W", "225444", "A", ""], dispatched.PayloadParts);
        Assert.Equal($"{sentence}\r\n", broadcast);
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
    public async Task SendAsync_BroadcastsStructuredMessageToConnectedClients()
    {
        await using var harness = await ServerHarness.StartAsync();
        await using var receiver = await harness.ConnectClientAsync();

        await harness.Server.SendAsync(
            new NmeaMessage("GPGLL", ["4916.45", "N", "12311.12", "W", "225444", "A", ""]));

        var received = await receiver.ReadRawLineAsync();

        Assert.Equal("$GPGLL,4916.45,N,12311.12,W,225444,A,*1D\r\n", received);
    }

    [Fact]
    public async Task SendAsync_PreservesConfiguredBangPrefix()
    {
        await using var harness = await ServerHarness.StartAsync();
        await using var receiver = await harness.ConnectClientAsync();

        await harness.Server.SendAsync(
            new NmeaMessage("GPGLL", ["4916.45", "N", "12311.12", "W", "225444", "A", ""], '!'));

        var received = await receiver.ReadRawLineAsync();

        Assert.Equal("!GPGLL,4916.45,N,12311.12,W,225444,A,*1D\r\n", received);
    }

    [Fact]
    public async Task RegisterHandlerAsync_IgnoresInvalidMessages()
    {
        await using var harness = await ServerHarness.StartAsync();
        await using var sender = await harness.ConnectClientAsync();
        var handlerTriggered = false;

        using var registration = harness.Server.RegisterHandler("GPGLL", (message, _) =>
        {
            handlerTriggered = true;
            return Task.CompletedTask;
        });

        await sender.SendLineAsync("invalid");
        await Task.Delay(150);

        Assert.False(handlerTriggered);
        Assert.False(harness.ServerTask.IsFaulted);
    }

    [Fact]
    public async Task RegisterHandlerAsync_ContinuesBroadcastWhenHandlerThrows()
    {
        await using var harness = await ServerHarness.StartAsync();
        await using var sender = await harness.ConnectClientAsync();
        await using var receiver = await harness.ConnectClientAsync();

        using var registration = harness.Server.RegisterHandler("GPGLL", (_, _) =>
            throw new InvalidOperationException("boom"));

        const string sentence = "$GPGLL,4916.45,N,12311.12,W,225444,A,*1D";

        await sender.SendLineAsync(sentence);

        var received = await receiver.ReadRawLineAsync();

        Assert.Equal($"{sentence}\r\n", received);
        Assert.False(harness.ServerTask.IsFaulted);
    }

    [Fact]
    public async Task RegisterHandlerAsync_DisposingOneHandlerKeepsOtherRegistrationsActive()
    {
        await using var harness = await ServerHarness.StartAsync();
        await using var sender = await harness.ConnectClientAsync();
        var firstCount = 0;
        var secondCount = 0;
        using var keepRegistration = harness.Server.RegisterHandler("GPGLL", (message, _) =>
        {
            Interlocked.Increment(ref secondCount);
            return Task.CompletedTask;
        });

        var disposeRegistration = harness.Server.RegisterHandler("GPGLL", (message, _) =>
        {
            Interlocked.Increment(ref firstCount);
            return Task.CompletedTask;
        });

        disposeRegistration.Dispose();

        await sender.SendLineAsync("$GPGLL,4916.45,N,12311.12,W,225444,A,*1D");
        await Task.Delay(150);

        Assert.Equal(0, firstCount);
        Assert.Equal(1, secondCount);
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

        public static Task<ServerHarness> StartAsync()
        {
            return StartAsync(options: null);
        }

        public static async Task<ServerHarness> StartAsync(NmeaTcpServerOptions? options)
        {
            var port = GetFreePort();
            var server = new NmeaTcpServer(port, options);
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

    private static async Task<string> CaptureConsoleAsync(Func<Task> action)
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);

        try
        {
            await action();
            await writer.FlushAsync();
            return writer.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static Task WaitForConsoleFlushAsync()
    {
        return Task.Delay(150);
    }
}
