namespace NmeaTransport.Clients;

/// <summary>
/// Defines the public contract for a reusable NMEA TCP client.
/// </summary>
public interface INmeaTcpClient : IAsyncDisposable
{
    /// <summary>
    /// Starts the client lifecycle and waits until the first connection succeeds.
    /// </summary>
    /// <param name="ct">A token used to cancel the initial connection attempt.</param>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Stops the client lifecycle and prevents further reconnection attempts.
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// Queues a structured NMEA message for transmission.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="ct">A token used to cancel queueing the message.</param>
    Task SendAsync(NmeaMessage message, CancellationToken ct = default);

    /// <summary>
    /// Registers an asynchronous handler for a specific NMEA header.
    /// </summary>
    /// <param name="header">The NMEA header to route to.</param>
    /// <param name="handler">The handler to invoke for matching messages.</param>
    /// <returns>An <see cref="IDisposable"/> that unregisters the handler when disposed.</returns>
    IDisposable RegisterHandler(string header, NmeaMessageHandler handler);
}
