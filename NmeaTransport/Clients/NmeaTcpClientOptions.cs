namespace NmeaTransport.Clients;

/// <summary>
/// Defines runtime options for <see cref="NmeaTcpClient"/>.
/// </summary>
public sealed class NmeaTcpClientOptions
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
    /// Gets or sets the delay between reconnection attempts.
    /// </summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or sets the timeout used while opening a TCP connection.
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the timeout used while writing a queued message to the socket.
    /// </summary>
    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(1);

    internal void Validate()
    {
        if (ReconnectDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ReconnectDelay), "Reconnect delay must be zero or positive.");
        }

        if (ConnectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectTimeout), "Connect timeout must be greater than zero.");
        }

        if (WriteTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(WriteTimeout), "Write timeout must be greater than zero.");
        }
    }
}
