using System.Globalization;
using NmeaTransport.Clients;

namespace NmeaTransport.Internal;

internal static class NmeaSentence
{
    private static readonly char[] ValidInitializers = ['$', '!'];

    internal static bool HasValidPrefix(string? sentence)
    {
        return !string.IsNullOrWhiteSpace(sentence) &&
               ValidInitializers.Contains(sentence[0]);
    }

    internal static bool ValidateChecksum(string sentence)
    {
        if (!HasValidPrefix(sentence))
        {
            return false;
        }

        var asterisk = sentence.IndexOf('*');

        if (asterisk <= 1 || asterisk != sentence.Length - 3)
        {
            return false;
        }

        var checksumText = sentence[(asterisk + 1)..];

        if (!byte.TryParse(checksumText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var expectedChecksum))
        {
            return false;
        }

        return CalculateChecksum(sentence.AsSpan(1, asterisk - 1)) == expectedChecksum;
    }

    internal static bool IsValidSentence(string? sentence, bool validateChecksum)
    {
        if (!HasValidPrefix(sentence))
        {
            return false;
        }

        if (sentence!.Length < 2)
        {
            return false;
        }

        return !validateChecksum || ValidateChecksum(sentence);
    }

    internal static string Serialize(NmeaMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var body = BuildBody(message.Header, message.PayloadParts);
        var checksum = CalculateChecksum(body.AsSpan());
        return $"${body}*{checksum:X2}";
    }

    internal static bool TryParse(
        string? sentence,
        bool validateChecksum,
        out NmeaMessage? message,
        out string? error)
    {
        if (!IsValidSentence(sentence, validateChecksum))
        {
            message = null;
            error = validateChecksum
                ? "Sentence has an invalid NMEA prefix or checksum."
                : "Sentence has an invalid NMEA prefix.";
            return false;
        }

        var body = ExtractBody(sentence!);

        if (body.Length == 0)
        {
            message = null;
            error = "Sentence body is empty.";
            return false;
        }

        var segments = body.Split(',', StringSplitOptions.None);
        message = new NmeaMessage(segments[0], segments.Skip(1).ToArray());
        error = null;
        return true;
    }

    private static string BuildBody(string header, IReadOnlyList<string> payloadParts)
    {
        if (payloadParts.Count == 0)
        {
            return header;
        }

        return $"{header},{string.Join(',', payloadParts)}";
    }

    private static string ExtractBody(string sentence)
    {
        var asteriskIndex = sentence.IndexOf('*');
        var bodyLength = (asteriskIndex >= 0 ? asteriskIndex : sentence.Length) - 1;
        return bodyLength <= 0 ? string.Empty : sentence.Substring(1, bodyLength);
    }

    private static int CalculateChecksum(ReadOnlySpan<char> body)
    {
        var checksum = 0;

        foreach (var ch in body)
        {
            checksum ^= ch;
        }

        return checksum;
    }
}
