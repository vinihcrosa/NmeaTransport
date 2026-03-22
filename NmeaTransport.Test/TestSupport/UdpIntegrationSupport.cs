using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NmeaTransport.Test.TestSupport;

internal sealed class RawUdpPeer : IAsyncDisposable
{
    private readonly Encoding _encoding = Encoding.ASCII;
    private readonly UdpClient _client;

    public RawUdpPeer(int? port = null, bool allowAddressReuse = false, IPAddress? bindAddress = null)
    {
        _client = new UdpClient(AddressFamily.InterNetwork);
        _client.Client.ExclusiveAddressUse = false;

        if (allowAddressReuse)
        {
            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }

        _client.EnableBroadcast = true;
        _client.Client.Bind(new IPEndPoint(bindAddress ?? IPAddress.Any, port ?? 0));
        Port = ((IPEndPoint)_client.Client.LocalEndPoint!).Port;
    }

    public int Port { get; }

    public async Task SendAsync(string datagram, int port, IPAddress? address = null)
    {
        var bytes = _encoding.GetBytes(datagram);
        await _client.SendAsync(bytes, bytes.Length, new IPEndPoint(address ?? IPAddress.Loopback, port)).ConfigureAwait(false);
    }

    public async Task<string> ReceiveAsync(TimeSpan? timeout = null)
    {
        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(3));
        var result = await _client.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
        return _encoding.GetString(result.Buffer);
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
