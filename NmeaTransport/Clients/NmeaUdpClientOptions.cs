using System.Net;

namespace NmeaTransport.Clients;

/// <summary>
/// Defines runtime options for <see cref="NmeaUdpClient"/>.
/// </summary>
public sealed class NmeaUdpClientOptions
{
    /// <summary>
    /// Gets or sets whether the client writes lifecycle and error logs to the terminal.
    /// </summary>
    /// <remarks>
    /// When <see langword="true"/>, the client writes logs to <see cref="Console"/>.
    /// When <see langword="false"/> or <see langword="null"/>, the client does not write terminal logs.
    /// </remarks>
    public bool? EnableLogging { get; set; } = false;

    /// <summary>
    /// Gets or sets whether received messages must have a valid checksum.
    /// </summary>
    public bool ValidateChecksum { get; set; }

    /// <summary>
    /// Gets or sets the receive timeout used to periodically re-check cancellation.
    /// </summary>
    public TimeSpan ReceiveTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the timeout used while sending UDP datagrams.
    /// </summary>
    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the default remote endpoint used by <see cref="NmeaUdpClient.SendAsync(NmeaMessage, CancellationToken)"/>.
    /// </summary>
    public IPEndPoint? DefaultRemoteEndPoint { get; set; }

    /// <summary>
    /// Gets or sets the broadcast address used by <see cref="NmeaUdpClient.SendBroadcastAsync(NmeaMessage, CancellationToken)"/>.
    /// </summary>
    public IPAddress BroadcastAddress { get; set; } = IPAddress.Broadcast;

    internal void Validate()
    {
        if (ReceiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ReceiveTimeout), "Receive timeout must be greater than zero.");
        }

        if (ReceiveTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(ReceiveTimeout), "Receive timeout is too large.");
        }

        if (WriteTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(WriteTimeout), "Write timeout must be greater than zero.");
        }

        if (WriteTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(WriteTimeout), "Write timeout is too large.");
        }

        if (BroadcastAddress is null)
        {
            throw new ArgumentNullException(nameof(BroadcastAddress));
        }
    }
}
