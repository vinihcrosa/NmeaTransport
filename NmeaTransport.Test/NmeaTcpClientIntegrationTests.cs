using System.Collections.Concurrent;
using NmeaTransport.Clients;
using NmeaTransport.Test.TestSupport;

namespace NmeaTransport.Test;

public class NmeaTcpClientIntegrationTests
{
    [Fact]
    public async Task SendAsync_ForwardsStructuredSentenceToConnectedServer()
    {
        await using var harness = new RestartableServerHarness();
        await harness.StartAsync();
        await using var receiver = await harness.ConnectClientAsync();
        await using var client = CreateClient(harness.Port);

        await client.ConnectAsync();
        await client.SendAsync(new NmeaMessage("GPGLL", ["4916.45", "N", "12311.12", "W", "225444", "A", ""]));

        var received = await receiver.ReadRawLineAsync();

        Assert.Equal("$GPGLL,4916.45,N,12311.12,W,225444,A,*1D\r\n", received);
    }

    [Fact]
    public async Task RegisterHandler_RoutesIncomingMessageByHeader()
    {
        await using var harness = new RestartableServerHarness();
        await harness.StartAsync();
        await using var sender = await harness.ConnectClientAsync();
        await using var client = CreateClient(harness.Port);
        var receivedMessage = new TaskCompletionSource<NmeaMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = client.RegisterHandler("GPGLL", (message, _) =>
        {
            receivedMessage.TrySetResult(message);
            return Task.CompletedTask;
        });

        await client.ConnectAsync();
        await sender.SendLineAsync(NmeaTestSentence.Create("GPGLL,4916.45,N,12311.12,W,225444,A,"));

        var message = await receivedMessage.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("GPGLL", message.Header);
        Assert.Equal(["4916.45", "N", "12311.12", "W", "225444", "A", ""], message.PayloadParts);
    }

    [Fact]
    public async Task ConnectAsync_CannotBeCalledTwiceOnTheSameInstance()
    {
        await using var harness = new RestartableServerHarness();
        await harness.StartAsync();
        await using var client = CreateClient(harness.Port);

        await client.ConnectAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task Client_ReconnectsAndKeepsHandlersRegistered()
    {
        await using var harness = new RestartableServerHarness();
        await harness.StartAsync();
        await using var client = CreateClient(
            harness.Port,
            new NmeaTcpClientOptions
            {
                ReconnectDelay = TimeSpan.FromMilliseconds(150),
                ConnectTimeout = TimeSpan.FromMilliseconds(150),
                WriteTimeout = TimeSpan.FromSeconds(1)
            });

        var receivedHeaders = new ConcurrentQueue<string>();
        var receivedSignal = new SemaphoreSlim(0, 2);

        using var registration = client.RegisterHandler("GPGLL", (message, _) =>
        {
            receivedHeaders.Enqueue(message.Header);
            receivedSignal.Release();
            return Task.CompletedTask;
        });

        await client.ConnectAsync();

        await using (var sender = await harness.ConnectClientAsync())
        {
            await sender.SendLineAsync(NmeaTestSentence.Create("GPGLL,1,2,3"));
            await WaitForSignalAsync(receivedSignal);
        }

        await harness.StopAsync();
        await Task.Delay(250);
        await harness.StartAsync();

        await using (var sender = await harness.ConnectClientAsync())
        {
            await WaitForDeliveryAfterReconnectAsync(sender, receivedSignal, NmeaTestSentence.Create("GPGLL,4,5,6"));
        }

        Assert.Equal(["GPGLL", "GPGLL"], receivedHeaders.ToArray());
    }

    [Fact]
    public async Task SendAsync_QueuesMessagesWhileDisconnectedAndFlushesAfterReconnect()
    {
        await using var harness = new RestartableServerHarness();
        await harness.StartAsync();
        await using var client = CreateClient(
            harness.Port,
            new NmeaTcpClientOptions
            {
                ReconnectDelay = TimeSpan.FromMilliseconds(400),
                ConnectTimeout = TimeSpan.FromMilliseconds(150),
                WriteTimeout = TimeSpan.FromSeconds(1)
            });
        var receivedMessage = new TaskCompletionSource<NmeaMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = client.RegisterHandler("GPRMC", (message, _) =>
        {
            receivedMessage.TrySetResult(message);
            return Task.CompletedTask;
        });

        await client.ConnectAsync();
        await harness.StopAsync();
        await Task.Delay(200);

        await client.SendAsync(new NmeaMessage("GPRMC", ["1", "2", "3"]));

        await harness.StartAsync();
        await harness.WaitForConnectedClientCountAsync(1);
        var received = await receivedMessage.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("GPRMC", received.Header);
        Assert.Equal(["1", "2", "3"], received.PayloadParts);
    }

    [Fact]
    public async Task DisconnectAsync_StopsFutureReconnectAttempts()
    {
        await using var harness = new RestartableServerHarness();
        await harness.StartAsync();
        await using var client = CreateClient(
            harness.Port,
            new NmeaTcpClientOptions
            {
                ReconnectDelay = TimeSpan.FromMilliseconds(150),
                ConnectTimeout = TimeSpan.FromMilliseconds(150),
                WriteTimeout = TimeSpan.FromSeconds(1)
            });

        var invocationCount = 0;

        using var registration = client.RegisterHandler("GPGLL", (message, _) =>
        {
            Interlocked.Increment(ref invocationCount);
            return Task.CompletedTask;
        });

        await client.ConnectAsync();
        await harness.StopAsync();
        await client.DisconnectAsync();
        await harness.StartAsync();

        await using (var sender = await harness.ConnectClientAsync())
        {
            await sender.SendLineAsync(NmeaTestSentence.Create("GPGLL,7,8,9"));
        }

        await Task.Delay(700);

        Assert.Equal(0, Volatile.Read(ref invocationCount));
    }

    [Fact]
    public async Task ChecksumValidation_WhenEnabled_DiscardsInvalidSentence()
    {
        await using var harness = new RestartableServerHarness();
        await harness.StartAsync();
        await using var sender = await harness.ConnectClientAsync();
        await using var client = CreateClient(
            harness.Port,
            new NmeaTcpClientOptions
            {
                ValidateChecksum = true,
                ReconnectDelay = TimeSpan.FromMilliseconds(150),
                ConnectTimeout = TimeSpan.FromMilliseconds(150),
                WriteTimeout = TimeSpan.FromSeconds(1)
            });

        var invocationCount = 0;

        using var registration = client.RegisterHandler("GPGLL", (message, _) =>
        {
            Interlocked.Increment(ref invocationCount);
            return Task.CompletedTask;
        });

        await client.ConnectAsync();
        await sender.SendLineAsync("$GPGLL,4916.45,N,12311.12,W,225444,A,*00");
        await Task.Delay(500);

        Assert.Equal(0, Volatile.Read(ref invocationCount));
    }

    [Fact]
    public async Task Logging_WhenOptionsAreOmitted_DoesNotWriteToConsole()
    {
        await using var server = new TestTcpPeerServer();
        await using var client = CreateClient(server.Port);

        var output = await CaptureConsoleAsync(async () =>
        {
            await client.ConnectAsync();
            await server.WaitForClientAsync();
            await server.SendLineAsync(NmeaTestSentence.Create("GPGLL,1,2,3"));
            await WaitForConsoleFlushAsync();
            await client.DisconnectAsync();
        });

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public async Task Logging_WhenSetToNull_DoesNotWriteToConsole()
    {
        await using var server = new TestTcpPeerServer();
        await using var client = CreateClient(
            server.Port,
            new NmeaTcpClientOptions
            {
                EnableLogging = null,
                ConnectTimeout = TimeSpan.FromMilliseconds(150),
                ReconnectDelay = TimeSpan.FromMilliseconds(150),
                WriteTimeout = TimeSpan.FromSeconds(1)
            });

        var output = await CaptureConsoleAsync(async () =>
        {
            await client.ConnectAsync();
            await server.WaitForClientAsync();
            await server.SendLineAsync(NmeaTestSentence.Create("GPGLL,1,2,3"));
            await WaitForConsoleFlushAsync();
            await client.DisconnectAsync();
        });

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public async Task Logging_WhenEnabled_WritesConnectionReceiveAndDisconnectMessages()
    {
        await using var server = new TestTcpPeerServer();
        await using var client = CreateClient(
            server.Port,
            new NmeaTcpClientOptions
            {
                EnableLogging = true,
                ConnectTimeout = TimeSpan.FromMilliseconds(150),
                ReconnectDelay = TimeSpan.FromMilliseconds(150),
                WriteTimeout = TimeSpan.FromSeconds(1)
            });

        var sentence = NmeaTestSentence.Create("GPGLL,1,2,3");
        var output = await CaptureConsoleAsync(async () =>
        {
            await client.ConnectAsync();
            await server.WaitForClientAsync();
            await server.SendLineAsync(sentence);
            await WaitForConsoleFlushAsync();
            await server.DisconnectClientAsync();
            await WaitForConsoleFlushAsync();
            await client.DisconnectAsync();
        });

        Assert.Contains($"Connected to 127.0.0.1:{server.Port}", output);
        Assert.Contains($"RX: {sentence}", output);
        Assert.Contains("Disconnected from server.", output);
    }

    [Fact]
    public async Task Logging_WhenEnabled_WritesHandlerErrorsToConsole()
    {
        await using var server = new TestTcpPeerServer();
        await using var client = CreateClient(
            server.Port,
            new NmeaTcpClientOptions
            {
                EnableLogging = true,
                ConnectTimeout = TimeSpan.FromMilliseconds(150),
                ReconnectDelay = TimeSpan.FromMilliseconds(150),
                WriteTimeout = TimeSpan.FromSeconds(1)
            });

        using var registration = client.RegisterHandler("GPGLL", (_, _) =>
        {
            throw new InvalidOperationException("boom");
        });

        var output = await CaptureConsoleAsync(async () =>
        {
            await client.ConnectAsync();
            await server.WaitForClientAsync();
            await server.SendLineAsync(NmeaTestSentence.Create("GPGLL,1,2,3"));
            await WaitForConsoleFlushAsync();
            await client.DisconnectAsync();
        });

        Assert.Contains("Handler error for header 'GPGLL': boom", output);
    }

    [Fact]
    public async Task Logging_WhenEnabled_WritesInvalidSentenceDiscardedMessage()
    {
        await using var server = new TestTcpPeerServer();
        await using var client = CreateClient(
            server.Port,
            new NmeaTcpClientOptions
            {
                EnableLogging = true,
                ValidateChecksum = true,
                ConnectTimeout = TimeSpan.FromMilliseconds(150),
                ReconnectDelay = TimeSpan.FromMilliseconds(150),
                WriteTimeout = TimeSpan.FromSeconds(1)
            });

        var output = await CaptureConsoleAsync(async () =>
        {
            await client.ConnectAsync();
            await server.WaitForClientAsync();
            await server.SendLineAsync("$GPGLL,4916.45,N,12311.12,W,225444,A,*00");
            await WaitForConsoleFlushAsync();
            await client.DisconnectAsync();
        });

        Assert.Contains("Invalid NMEA sentence discarded:", output);
        Assert.Contains("Raw='$GPGLL,4916.45,N,12311.12,W,225444,A,*00'", output);
    }

    private static NmeaTcpClient CreateClient(int port, NmeaTcpClientOptions? options = null)
    {
        return new NmeaTcpClient("127.0.0.1", port, options);
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

    private static async Task WaitForSignalAsync(SemaphoreSlim semaphore)
    {
        var acquired = await semaphore.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(acquired, "Expected client handler to receive the message within the timeout.");
    }

    private static Task WaitForConsoleFlushAsync()
    {
        return Task.Delay(150);
    }

    private static async Task WaitForDeliveryAfterReconnectAsync(
        RawTcpClient sender,
        SemaphoreSlim semaphore,
        string sentence)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));

        while (!timeoutCts.IsCancellationRequested)
        {
            await sender.SendLineAsync(sentence);

            if (await semaphore.WaitAsync(TimeSpan.FromMilliseconds(250), timeoutCts.Token))
            {
                return;
            }
        }

        Assert.Fail("Expected client handler to receive the message after reconnection.");
    }
}
