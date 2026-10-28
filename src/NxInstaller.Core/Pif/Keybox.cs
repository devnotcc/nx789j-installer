using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;

namespace NxInstaller.Core.Pif;

public enum KeyboxVerdict { Good, ExpiringSoon, Expired, Revoked, Invalid }

public sealed record KeyboxKey(string Algorithm, IReadOnlyList<X509Certificate2> Chain, bool KeyMatchesLeaf, string? Problem)
{
    public X509Certificate2 Leaf => Chain[0];
    public string LeafSerial => RevocationList.Normalize(Leaf.SerialNumber);
}

public sealed record KeyboxReport(string DeviceId, IReadOnlyList<KeyboxKey> Keys, KeyboxVerdict Verdict, IReadOnlyList<string> Findings)
{
    /// <summary>Safe to install: well-formed, chains to a Google root, not listed as revoked, still valid.</summary>
    public bool Usable => Verdict is KeyboxVerdict.Good or KeyboxVerdict.ExpiringSoon;
}

/// <summary>
/// Validates a keybox.xml the user chose in the toolbox. The app never ships or downloads keyboxes
/// of its own; it only inspects the file the user points at, so they aren't surprised by one that
/// Google has already revoked or that has expired — either of which makes Play Integrity fail every
/// verdict once it is installed.
///
/// A "usable" result means only that the file is consistent, chains to a known Google attestation
/// root, and none of its certificates appear on Google's public revocation list today. It cannot
/// promise Google will honor it; publicly shared keyboxes are typically revoked within days.
/// Certificate private-key material is never written to the log.
/// </summary>
public static class Keybox
{
    public static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(14);

    public static KeyboxReport Check(string xml, RevocationList? crl, DateTimeOffset now)
    {
        var findings = new List<string>();
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException e)
        {
            return new KeyboxReport("", [], KeyboxVerdict.Invalid, [$"Not a valid XML keybox file: {e.Message}"]);
        }

        var deviceId = doc.Descendants("Keybox").FirstOrDefault()?.Attribute("DeviceID")?.Value ?? "";
        var keyNodes = doc.Descendants("Key").ToList();
        if (keyNodes.Count == 0)
            return new KeyboxReport(deviceId, [], KeyboxVerdict.Invalid, ["No <Key> entries found in the keybox."]);

        var keys = new List<KeyboxKey>();
        foreach (var kn in keyNodes)
        {
            var algo = kn.Attribute("algorithm")?.Value ?? "?";
            var chain = kn.Descendants("Certificate")
                .Select(c => c.Value.Trim())
                .Select(TryLoadCert)
                .Where(c => c is not null)
                .Select(c => c!)
                .ToList();

            if (chain.Count == 0)
            {
                keys.Add(new KeyboxKey(algo, [], false, "no readable certificates"));
                continue;
            }

            var privatePem = kn.Element("PrivateKey")?.Value.Trim();
            var matches = privatePem is not null && KeyMatchesLeaf(privatePem, chain[0]);
            var problem = ChainProblem(chain, now);
            keys.Add(new KeyboxKey(algo, chain, matches, problem));
        }

        // Overall verdict: the worst thing found across the keys, revoked being worst.
        var verdict = KeyboxVerdict.Good;
        foreach (var k in keys)
        {
            if (k.Chain.Count == 0) { verdict = Worst(verdict, KeyboxVerdict.Invalid); findings.Add($"{k.Algorithm}: {k.Problem}"); continue; }

            var rootInfo = RootOf(k.Chain);
            if (rootInfo is null)
            {
                verdict = Worst(verdict, KeyboxVerdict.Invalid);
                findings.Add($"{k.Algorithm}: the certificate chain does not end at a Google attestation root (this keybox will not be trusted).");
            }
            else findings.Add($"{k.Algorithm}: chains to {rootInfo}.");

            if (!k.KeyMatchesLeaf)
            {
                verdict = Worst(verdict, KeyboxVerdict.Invalid);
                findings.Add($"{k.Algorithm}: the private key does not match its certificate.");
            }
            if (k.Problem is not null && k.Chain.Count > 0)
                findings.Add($"{k.Algorithm}: {k.Problem}");

            // Expiry
            var leaf = k.Leaf;
            if (now > leaf.NotAfter)
            {
                verdict = Worst(verdict, KeyboxVerdict.Expired);
                findings.Add($"{k.Algorithm}: the certificate expired on {leaf.NotAfter:yyyy-MM-dd}.");
            }
            else if (leaf.NotAfter - now < ExpiryWarning)
            {
                verdict = Worst(verdict, KeyboxVerdict.ExpiringSoon);
                findings.Add($"{k.Algorithm}: the certificate expires soon ({leaf.NotAfter:yyyy-MM-dd}).");
            }

            // Revocation: any certificate in the chain being listed poisons the keybox.
            if (crl is not null)
            {
                foreach (var c in k.Chain)
                {
                    if (crl.StatusOf(c.SerialNumber) is { } st)
                    {
                        verdict = Worst(verdict, KeyboxVerdict.Revoked);
                        findings.Add($"{k.Algorithm}: certificate {RevocationList.Normalize(c.SerialNumber)} is {st} on Google's revocation list.");
                    }
                }
            }
            else findings.Add("Revocation list not checked (offline); install at your own risk.");
        }

        return new KeyboxReport(deviceId, keys, verdict, findings);
    }

    static string? ChainProblem(IReadOnlyList<X509Certificate2> chain, DateTimeOffset now)
    {
        for (var i = 0; i < chain.Count - 1; i++)
        {
            // Each cert should be issued by the next one up.
            if (chain[i].IssuerName.RawData.AsSpan().SequenceEqual(chain[i + 1].SubjectName.RawData) == false)
                return $"certificate {i} is not issued by certificate {i + 1} (broken chain)";
        }
        return null;
    }

    /// <summary>Returns a description of the Google root the chain ends at, or null if it isn't one.</summary>
    static string? RootOf(IReadOnlyList<X509Certificate2> chain)
    {
        var top = chain[^1];
        var spki = System.Security.Cryptography.SHA256.HashData(top.PublicKey.ExportSubjectPublicKeyInfo());
        return GoogleRoots.GetValueOrDefault(Convert.ToHexStringLower(spki));
    }

    /// <summary>SHA-256 of the SubjectPublicKeyInfo of Google's two attestation roots.</summary>
    public static readonly IReadOnlyDictionary<string, string> GoogleRoots = new Dictionary<string, string>
    {
        ["feb2ea7551ee316ed4bb443c8293b884dbfdea40b603ee3e4f4a897e4580fbae"] = "Google hardware attestation root (RSA)",
        ["3ee44512a1af2beb39c889490c60ea3f82e43f5d5a5532f5ab9419f676cd07ec"] = "Google Key Attestation CA1 (EC, 2025)",
    };

    static X509Certificate2? TryLoadCert(string pem)
    {
        try { return X509Certificate2.CreateFromPem(pem); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    static bool KeyMatchesLeaf(string privatePem, X509Certificate2 leaf)
    {
        try
        {
            using var withKey = X509Certificate2.CreateFromPem(
                $"-----BEGIN CERTIFICATE-----\n{Convert.ToBase64String(leaf.RawData)}\n-----END CERTIFICATE-----", privatePem);
            return withKey.GetPublicKey().AsSpan().SequenceEqual(leaf.GetPublicKey());
        }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
        catch (ArgumentException) { return false; }
    }

    static KeyboxVerdict Worst(KeyboxVerdict a, KeyboxVerdict b) => (KeyboxVerdict)Math.Max((int)a, (int)b);
}
