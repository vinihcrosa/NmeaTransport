using NmeaTransport.Server;

namespace NmeaTransport.Test;

public class NmeaTcpServerValidationTests
{
    [Theory]
    [InlineData("$GPGLL,4916.45,N,12311.12,W,225444,A")]
    [InlineData("!AIVDM,1,1,,A,15MvqR0P00PD;88MD5MTDwvN0<0u,0*4D")]
    public void HasValidNmeaPrefix_AcceptsSupportedPrefixes(string sentence)
    {
        Assert.True(NmeaTcpServer.HasValidNmeaPrefix(sentence));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("GPGLL,4916.45,N,12311.12,W,225444,A")]
    [InlineData("*6A")]
    public void HasValidNmeaPrefix_RejectsMissingOrInvalidPrefix(string? sentence)
    {
        Assert.False(NmeaTcpServer.HasValidNmeaPrefix(sentence));
    }

    [Fact]
    public void ValidateChecksum_ReturnsTrueForKnownValidSentence()
    {
        const string sentence = "$GPGLL,4916.45,N,12311.12,W,225444,A,*1D";

        Assert.True(NmeaTcpServer.ValidateChecksum(sentence));
    }

    [Theory]
    [InlineData("$GPGLL,4916.45,N,12311.12,W,225444,A,*00")]
    [InlineData("$GPGLL,4916.45,N,12311.12,W,225444,A")]
    [InlineData("$GPGLL,4916.45,N,12311.12,W,225444,A,*ZZ")]
    public void ValidateChecksum_ReturnsFalseForInvalidChecksum(string sentence)
    {
        Assert.False(NmeaTcpServer.ValidateChecksum(sentence));
    }

    [Theory]
    [InlineData("$GPGLL,4916.45,N,12311.12,W,225444,A")]
    [InlineData("!AIVDM,1,1,,A,15MvqR0P00PD;88MD5MTDwvN0<0u,0")]
    public void IsValidSentence_AcceptsValidNmeaSentence(string sentence)
    {
        Assert.True(NmeaTcpServer.IsValidSentence(sentence));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad-data")]
    [InlineData("$")]
    [InlineData("$GPGLL,4916.45,N,12311.12,W,225444,A,*00")]
    public void IsValidSentence_RejectsInvalidInput(string sentence)
    {
        Assert.False(NmeaTcpServer.IsValidSentence(sentence));
    }
}
