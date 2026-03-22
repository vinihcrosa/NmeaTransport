using System.Net;

namespace NmeaTransport.Clients;

/// <summary>
/// Defines the public contract for a reusable NMEA UDP client.
/// </summary>
public interface INmeaUdpClient : IAsyncDisposable
{
    /// <summary>
    /// Starts the UDP listener lifecycle and prepares the socket for sending and receiving datagrams.
    /// </summary>
    /// <param name="ct">A token used to cancel the initial startup operation.</param>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Stops the UDP listener lifecycle and releases the underlying socket.
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// Sends a structured NMEA message to the default remote endpoint configured in the client options.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="ct">A token used to cancel the send request before it is issued.</param>
    Task SendAsync(NmeaMessage message, CancellationToken ct = default);

    /// <summary>
    /// Sends a structured NMEA message to a specific remote UDP endpoint.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="remoteEndPoint">The target endpoint.</param>
    /// <param name="ct">A token used to cancel the send request before it is issued.</param>
    Task SendAsync(NmeaMessage message, IPEndPoint remoteEndPoint, CancellationToken ct = default);

    /// <summary>
    /// Sends a structured NMEA message as a UDP broadcast using the client's configured port.
    /// </summary>
    /// <param name="message">The message to broadcast.</param>
    /// <param name="ct">A token used to cancel the send request before it is issued.</param>
    Task SendBroadcastAsync(NmeaMessage message, CancellationToken ct = default);

    /// <summary>
    /// Registers an asynchronous handler for a specific NMEA header.
    /// </summary>
    /// <param name="header">The NMEA header to route to.</param>
    /// <param name="handler">The handler to invoke for matching messages.</param>
    /// <returns>An <see cref="IDisposable"/> that unregisters the handler when disposed.</returns>
    IDisposable RegisterHandler(string header, NmeaMessageHandler handler);
}
