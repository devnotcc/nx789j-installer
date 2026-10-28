using NxInstaller.Core.Pif;
using Xunit;

namespace NxInstaller.Core.Tests;

public class RevocationListTests
{
    const string Sample = """
        { "entries": {
            "806b52bbc8ddb62a5f43742401412562": { "status": "REVOKED", "reason": "KEY_COMPROMISE" },
            "00ff": { "status": "SUSPENDED" }
        } }
        """;

    [Fact]
    public void ParsesEntries()
    {
        var crl = RevocationList.Parse(Sample);
        Assert.Equal("REVOKED", crl.StatusOf("806b52bbc8ddb62a5f43742401412562"));
        Assert.True(crl.IsListed("806b52bbc8ddb62a5f43742401412562"));
    }

    [Theory]
    [InlineData("00FF")]     // uppercase
    [InlineData("0000ff")]   // leading zeros
    [InlineData("ff")]
    public void NormalizesSerials(string serial) =>
        Assert.Equal("SUSPENDED", RevocationList.Parse(Sample).StatusOf(serial));

    [Fact]
    public void UnlistedSerialIsNull() =>
        Assert.Null(RevocationList.Parse(Sample).StatusOf("deadbeef"));

    [Fact]
    public void EmptyListParses() =>
        Assert.False(RevocationList.Parse("""{"entries":{}}""").IsListed("abc"));
}
