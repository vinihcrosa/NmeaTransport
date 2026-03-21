namespace NmeaTransport.Clients;

/// <summary>
/// Represents an asynchronous handler for a routed NMEA message.
/// </summary>
/// <param name="message">The received NMEA message.</param>
/// <param name="cancellationToken">A token used to cancel message processing.</param>
public delegate Task NmeaMessageHandler(NmeaMessage message, CancellationToken cancellationToken);
