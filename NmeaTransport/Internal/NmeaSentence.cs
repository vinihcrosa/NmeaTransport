using System.Globalization;
using NmeaTransport.Clients;

namespace NmeaTransport.Internal;

internal static class NmeaSentence
{
    private static readonly char[] ValidInitializers = { '$', '!' };

    internal static bool HasValidPrefix(string? sentence)
    {
        var normalizedSentence = NormalizeLeadingNoise(sentence);
        return !string.IsNullOrWhiteSpace(normalizedSentence) &&
               ValidInitializers.Contains(normalizedSentence[0]);
    }

    internal static bool ValidateChecksum(string sentence)
    {
        var normalizedSentence = NormalizeLeadingNoise(sentence);

        if (!HasValidPrefix(normalizedSentence))
        {
            return false;
        }

        var asterisk = normalizedSentence!.IndexOf('*');

        if (asterisk <= 1 || asterisk != normalizedSentence.Length - 3)
        {
            return false;
        }

        var checksumText = normalizedSentence[(asterisk + 1)..];

        if (!byte.TryParse(checksumText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var expectedChecksum))
        {
            return false;
        }

        return CalculateChecksum(normalizedSentence.AsSpan(1, asterisk - 1)) == expectedChecksum;
    }

    internal static bool IsValidSentence(string? sentence, bool validateChecksum)
    {
        var normalizedSentence = NormalizeLeadingNoise(sentence);

        if (!HasValidPrefix(normalizedSentence))
        {
            return false;
        }

        if (normalizedSentence!.Length < 2)
        {
            return false;
        }

        return !validateChecksum || ValidateChecksum(normalizedSentence);
    }

    internal static string Serialize(NmeaMessage message)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

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
        var normalizedSentence = NormalizeLeadingNoise(sentence);

        if (!IsValidSentence(normalizedSentence, validateChecksum))
        {
            message = null;
            error = validateChecksum
                ? "Sentence has an invalid NMEA prefix or checksum."
                : "Sentence has an invalid NMEA prefix.";
            return false;
        }

        var body = ExtractBody(normalizedSentence!);

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

    private static string? NormalizeLeadingNoise(string? sentence)
    {
        if (string.IsNullOrWhiteSpace(sentence))
        {
            return sentence;
        }

        var startIndex = sentence.IndexOfAny(ValidInitializers);
        return startIndex < 0 ? sentence : sentence[startIndex..];
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
