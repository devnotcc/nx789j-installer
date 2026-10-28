using NxInstaller.Core.Pif;
using Xunit;

namespace NxInstaller.Core.Tests;

/// <summary>
/// These read real keybox files from tests/.../data, which hold private keys and are never
/// committed (see .gitignore). When the files are absent the real-keybox cases are skipped; the
/// structural, CRL and expiry logic is also covered without them (NonXml/EmptyKeybox and the
/// RevocationList/Keybox logic tests).
/// </summary>
public class KeyboxTests
{
    static string Data(string f) => Path.Combine(AppContext.BaseDirectory, "data", f);
    static bool Has(string f) => File.Exists(Data(f));
    static string Read(string f) => File.ReadAllText(Data(f));
    static readonly DateTimeOffset Now = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);

    // Google's two attestation roots, by their SubjectPublicKeyInfo hash.
    [Fact]
    public void KnowsTwoGoogleRoots() => Assert.Equal(2, Keybox.GoogleRoots.Count);

    [Fact]
    public void GoodKeyboxChainsToGoogleRootAndIsUsable()
    {
        if (!Has("keybox-good.xml")) return; // local-only test data
        var rep = Keybox.Check(Read("keybox-good.xml"), crl: null, Now);
        Assert.Single(rep.Keys);
        var key = rep.Keys[0];
        Assert.True(key.KeyMatchesLeaf);
        // chains to the RSA root, valid to 2033, private key matches
        Assert.Contains(rep.Findings, f => f.Contains("Google hardware attestation root"));
        Assert.Equal(KeyboxVerdict.Good, rep.Verdict);
        Assert.True(rep.Usable);
    }

    [Fact]
    public void RevokedSerialMakesItUnusable()
    {
        if (!Has("keybox-revoked.xml")) return; // local-only test data
        // The known-revoked leaf of keybox-revoked.xml.
        var crl = RevocationList.Parse("""{"entries":{"806b52bbc8ddb62a5f43742401412562":{"status":"REVOKED"}}}""");
        var rep = Keybox.Check(Read("keybox-revoked.xml"), crl, Now);
        Assert.Equal(KeyboxVerdict.Revoked, rep.Verdict);
        Assert.False(rep.Usable);
        Assert.Contains(rep.Findings, f => f.Contains("REVOKED"));
    }

    [Fact]
    public void ExpiredKeyboxIsNotUsable()
    {
        if (!Has("keybox-expired.xml")) return; // local-only test data
        // keybox-expired leaf expired 2026-09-26; checking after that date.
        var rep = Keybox.Check(Read("keybox-expired.xml"), crl: null, Now);
        Assert.Equal(KeyboxVerdict.Expired, rep.Verdict);
        Assert.False(rep.Usable);
    }

    [Fact]
    public void GoodKeyboxBeforeExpiryWithCleanCrlStaysGood()
    {
        if (!Has("keybox-good.xml")) return; // local-only test data
        var crl = RevocationList.Parse("""{"entries":{}}""");
        var rep = Keybox.Check(Read("keybox-good.xml"), crl, Now);
        Assert.True(rep.Usable);
    }

    [Fact]
    public void NonXmlIsInvalid()
    {
        var rep = Keybox.Check("this is not xml", null, Now);
        Assert.Equal(KeyboxVerdict.Invalid, rep.Verdict);
        Assert.False(rep.Usable);
    }

    [Fact]
    public void EmptyKeyboxIsInvalid()
    {
        var rep = Keybox.Check("<AndroidAttestation></AndroidAttestation>", null, Now);
        Assert.Equal(KeyboxVerdict.Invalid, rep.Verdict);
    }

    [Fact]
    public void OfflineCheckWarnsRevocationNotChecked()
    {
        if (!Has("keybox-good.xml")) return; // local-only test data
        var rep = Keybox.Check(Read("keybox-good.xml"), crl: null, Now);
        Assert.Contains(rep.Findings, f => f.Contains("Revocation list not checked"));
    }
}
