using NmeaTransport.Internal;

namespace NmeaTransport.Clients;

/// <summary>
/// Represents a structured NMEA sentence payload.
/// </summary>
public sealed class NmeaMessage
{
    /// <summary>
    /// Creates a new instance of <see cref="NmeaMessage"/>.
    /// </summary>
    /// <param name="header">The sentence header used for routing and serialization.</param>
    /// <param name="payloadParts">The payload segments that follow the header.</param>
    public NmeaMessage(string header, IReadOnlyList<string> payloadParts)
        : this(header, payloadParts, '$')
    {
    }

    /// <summary>
    /// Creates a new instance of <see cref="NmeaMessage"/>.
    /// </summary>
    /// <param name="header">The sentence header used for routing and serialization.</param>
    /// <param name="payloadParts">The payload segments that follow the header.</param>
    /// <param name="prefix">The NMEA sentence prefix used during serialization.</param>
    public NmeaMessage(string header, IReadOnlyList<string> payloadParts, char prefix)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            throw new ArgumentException("The NMEA header must not be null or whitespace.", nameof(header));
        }

        if (payloadParts is null)
        {
            throw new ArgumentNullException(nameof(payloadParts));
        }

        if (payloadParts.Any(part => part is null))
        {
            throw new ArgumentException("Payload parts must not contain null values.", nameof(payloadParts));
        }

        if (!NmeaSentence.IsSupportedPrefix(prefix))
        {
            throw new ArgumentOutOfRangeException(nameof(prefix), prefix, "The NMEA prefix must be '$' or '!'.");
        }

        Header = header;
        PayloadParts = payloadParts.ToArray();
        Prefix = prefix;
    }

    /// <summary>
    /// Gets the NMEA sentence prefix.
    /// </summary>
    public char Prefix { get; }

    /// <summary>
    /// Gets the NMEA header.
    /// </summary>
    public string Header { get; }

    /// <summary>
    /// Gets the NMEA payload parts.
    /// </summary>
    public IReadOnlyList<string> PayloadParts { get; }
}
