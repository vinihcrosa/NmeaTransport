using NmeaTransport.Clients;
using NmeaTransport.Internal;

namespace NmeaTransport.Test;

public class NmeaSentenceTests
{
    [Theory]
    [InlineData("$GPGLL,4916.45,N,12311.12,W,225444,A")]
    [InlineData("!AIVDM,1,1,,A,15MvqR0P00PD;88MD5MTDwvN0<0u,0*4D")]
    [InlineData("  20$TESTE,teste,16*27")]
    public void HasValidPrefix_AcceptsSupportedPrefixes(string sentence)
    {
        Assert.True(NmeaSentence.HasValidPrefix(sentence));
    }

    [Fact]
    public void Serialize_BuildsSentenceWithChecksum()
    {
        var message = new NmeaMessage("GPGLL", ["4916.45", "N", "12311.12", "W", "225444", "A", ""]);

        var sentence = NmeaSentence.Serialize(message);

        Assert.Equal("$GPGLL,4916.45,N,12311.12,W,225444,A,*1D", sentence);
    }

    [Fact]
    public void Serialize_UsesConfiguredPrefix()
    {
        var message = new NmeaMessage("GPGLL", ["4916.45", "N", "12311.12", "W", "225444", "A", ""], '!');

        var sentence = NmeaSentence.Serialize(message);

        Assert.Equal("!GPGLL,4916.45,N,12311.12,W,225444,A,*1D", sentence);
    }

    [Fact]
    public void TryParse_ReturnsStructuredMessage()
    {
        const string sentence = "$GPGLL,4916.45,N,12311.12,W,225444,A,*1D";

        var parsed = NmeaSentence.TryParse(sentence, validateChecksum: true, out var message, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.NotNull(message);
        Assert.Equal('$', message!.Prefix);
        Assert.Equal("GPGLL", message!.Header);
        Assert.Equal(["4916.45", "N", "12311.12", "W", "225444", "A", ""], message.PayloadParts);
    }

    [Fact]
    public void TryParse_PreservesConfiguredPrefix()
    {
        const string sentence = "!GPGLL,4916.45,N,12311.12,W,225444,A,*1D";

        var parsed = NmeaSentence.TryParse(sentence, validateChecksum: true, out var message, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.NotNull(message);
        Assert.Equal('!', message!.Prefix);
        Assert.Equal("GPGLL", message.Header);
        Assert.Equal(["4916.45", "N", "12311.12", "W", "225444", "A", ""], message.PayloadParts);
    }

    [Fact]
    public void TryParse_AcceptsInvalidChecksumWhenValidationIsDisabled()
    {
        const string sentence = "$GPGLL,4916.45,N,12311.12,W,225444,A,*00";

        var parsed = NmeaSentence.TryParse(sentence, validateChecksum: false, out var message, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.NotNull(message);
    }

    [Fact]
    public void TryParse_IgnoresLeadingNoiseBeforeSentencePrefix()
    {
        const string sentence = "  20$TESTE,teste,16*27";

        var parsed = NmeaSentence.TryParse(sentence, validateChecksum: true, out var message, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.NotNull(message);
        Assert.Equal("TESTE", message!.Header);
        Assert.Equal(["teste", "16"], message.PayloadParts);
    }

    [Fact]
    public void TryParse_RejectsInvalidChecksumWhenValidationIsEnabled()
    {
        const string sentence = "$GPGLL,4916.45,N,12311.12,W,225444,A,*00";

        var parsed = NmeaSentence.TryParse(sentence, validateChecksum: true, out var message, out var error);

        Assert.False(parsed);
        Assert.Null(message);
        Assert.NotNull(error);
    }

    [Fact]
    public void Constructor_RejectsUnsupportedPrefix()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new NmeaMessage("GPGLL", ["1", "2", "3"], '?'));

        Assert.Equal("prefix", exception.ParamName);
        Assert.Contains("must be '$' or '!'", exception.Message);
    }
}
