using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using NmeaTransport.Clients;
using NmeaTransport.Test.TestSupport;

namespace NmeaTransport.Test;

public class NmeaUdpClientIntegrationTests
{
    [Fact]
    public async Task SendAsync_ForwardsStructuredSentenceToDirectedEndpoint()
    {
        await using var receiver = new RawUdpPeer();
        await using var client = CreateClient(receiver.Port);

        await client.ConnectAsync();
        await client.SendAsync(new NmeaMessage("GPGLL", ["4916.45", "N", "12311.12", "W", "225444", "A", ""]), new IPEndPoint(IPAddress.Loopback, receiver.Port));

        var received = await receiver.ReceiveAsync();

        Assert.Equal("$GPGLL,4916.45,N,12311.12,W,225444,A,*1D", received);
    }

    [Fact]
    public async Task SendAsync_UsesDefaultRemoteEndpointWhenConfigured()
    {
        await using var receiver = new RawUdpPeer();
        await using var client = CreateClient(
            GetFreePort(),
            new NmeaUdpClientOptions
            {
                DefaultRemoteEndPoint = new IPEndPoint(IPAddress.Loopback, receiver.Port)
            });

        await client.ConnectAsync();
        await client.SendAsync(new NmeaMessage("GPRMC", ["1", "2", "3"]));

        var received = await receiver.ReceiveAsync();

        Assert.Equal(NmeaTestSentence.Create("GPRMC,1,2,3"), received);
    }

    [Fact]
    public async Task SendAsync_WithoutDefaultRemoteEndpoint_Throws()
    {
        await using var client = CreateClient(GetFreePort());

        await client.ConnectAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(new NmeaMessage("GPGLL", ["1"])));

        Assert.Equal("A default remote endpoint is not configured for this UDP client.", exception.Message);
    }

    [Fact]
    public async Task RegisterHandler_RoutesIncomingMessageByHeader()
    {
        await using var sender = new RawUdpPeer();
        var port = GetFreePort();
        await using var client = CreateClient(port);
        var receivedMessage = new TaskCompletionSource<NmeaMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = client.RegisterHandler("GPGLL", (message, _) =>
        {
            receivedMessage.TrySetResult(message);
            return Task.CompletedTask;
        });

        await client.ConnectAsync();
        await sender.SendAsync(NmeaTestSentence.Create("GPGLL,4916.45,N,12311.12,W,225444,A,"), port);

        var message = await receivedMessage.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("GPGLL", message.Header);
        Assert.Equal(["4916.45", "N", "12311.12", "W", "225444", "A", ""], message.PayloadParts);
    }

    [Fact]
    public async Task ChecksumValidation_WhenEnabled_DiscardsInvalidSentence()
    {
        await using var sender = new RawUdpPeer();
        var port = GetFreePort();
        await using var client = CreateClient(
            port,
            new NmeaUdpClientOptions
            {
                ValidateChecksum = true
            });

        var invocationCount = 0;

        using var registration = client.RegisterHandler("GPGLL", (_, _) =>
        {
            Interlocked.Increment(ref invocationCount);
            return Task.CompletedTask;
        });

        await client.ConnectAsync();
        await sender.SendAsync("$GPGLL,4916.45,N,12311.12,W,225444,A,*00", port);
        await Task.Delay(300);

        Assert.Equal(0, Volatile.Read(ref invocationCount));
    }

    [Fact]
    public async Task ConnectAsync_CannotBeCalledTwiceAndCannotRestartAfterDisconnect()
    {
        await using var client = CreateClient(GetFreePort());

        await client.ConnectAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync());

        await client.DisconnectAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task SendBroadcastAsync_TransmitsBroadcastDatagramOnClientPort()
    {
        var port = GetFreePort();
        await using var receiver = new RawUdpPeer(port, allowAddressReuse: true);
        await using var client = CreateClient(
            port,
            new NmeaUdpClientOptions
            {
                BroadcastAddress = IPAddress.Broadcast
            });

        await client.ConnectAsync();
        await client.SendBroadcastAsync(new NmeaMessage("GPRMC", ["1", "2", "3"]));

        var received = await receiver.ReceiveAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(NmeaTestSentence.Create("GPRMC,1,2,3"), received);
    }

    [Fact]
    public async Task HandlerErrors_AreLoggedAndDoNotStopFutureReceives()
    {
        await using var sender = new RawUdpPeer();
        var port = GetFreePort();
        await using var client = CreateClient(
            port,
            new NmeaUdpClientOptions
            {
                EnableLogging = true
            });

        var successfulHeaders = new ConcurrentQueue<string>();
        var signal = new SemaphoreSlim(0, 1);

        using var firstRegistration = client.RegisterHandler("GPGLL", (_, _) =>
        {
            throw new InvalidOperationException("boom");
        });

        using var secondRegistration = client.RegisterHandler("GPRMC", (message, _) =>
        {
            successfulHeaders.Enqueue(message.Header);
            signal.Release();
            return Task.CompletedTask;
        });

        var output = await CaptureConsoleAsync(async () =>
        {
            await client.ConnectAsync();
            await sender.SendAsync(NmeaTestSentence.Create("GPGLL,1,2,3"), port);
            await WaitForConsoleFlushAsync();
            await sender.SendAsync(NmeaTestSentence.Create("GPRMC,4,5,6"), port);
            var acquired = await signal.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(acquired, "Expected UDP handler to receive the follow-up message.");
            await client.DisconnectAsync();
        });

        Assert.Contains("Handler error for header 'GPGLL': boom", output);
        Assert.Equal(["GPRMC"], successfulHeaders.ToArray());
    }

    [Fact]
    public async Task Logging_WhenEnabled_WritesLifecycleReceiveAndInvalidSentenceMessages()
    {
        await using var sender = new RawUdpPeer();
        var port = GetFreePort();
        await using var client = CreateClient(
            port,
            new NmeaUdpClientOptions
            {
                EnableLogging = true,
                ValidateChecksum = true
            });

        var sentence = NmeaTestSentence.Create("GPGLL,1,2,3");
        var output = await CaptureConsoleAsync(async () =>
        {
            await client.ConnectAsync();
            await sender.SendAsync(sentence, port);
            await WaitForConsoleFlushAsync();
            await sender.SendAsync("$GPGLL,1,2,3*00", port);
            await WaitForConsoleFlushAsync();
            await client.DisconnectAsync();
        });

        Assert.Contains($"Listening on UDP port {port}", output);
        Assert.Contains($"RX: {sentence}", output);
        Assert.Contains("Invalid NMEA sentence discarded:", output);
        Assert.Contains($"Stopped listening on UDP port {port}", output);
    }

    private static NmeaUdpClient CreateClient(int port, NmeaUdpClientOptions? options = null)
    {
        return new NmeaUdpClient(port, options);
    }

    private static int GetFreePort()
    {
        using var udpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)udpClient.Client.LocalEndPoint!).Port;
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
