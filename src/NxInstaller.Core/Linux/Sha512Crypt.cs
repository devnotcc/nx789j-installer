using System.Security.Cryptography;
using System.Text;

namespace NxInstaller.Core.Linux;

/// <summary>
/// SHA-512 crypt ("$6$", Ulrich Drepper's specification), the password hash format of
/// /etc/shadow. The installer hashes the Linux password on the PC, so only the hash ever reaches
/// the phone (Debian applies it with "chpasswd -e" on its first start).
/// </summary>
public static class Sha512Crypt
{
    const string Itoa64 = "./0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    public const int DefaultRounds = 5000;

    /// <summary>Hash with a fresh random 16-character salt.</summary>
    public static string Hash(string password, int rounds = DefaultRounds)
    {
        var salt = new char[16];
        for (var i = 0; i < salt.Length; i++) salt[i] = Itoa64[RandomNumberGenerator.GetInt32(Itoa64.Length)];
        return Hash(password, new string(salt), rounds);
    }

    /// <summary>Hash with the given salt (at most 16 characters are used). Rounds other than 5000 are written into the result.</summary>
    public static string Hash(string password, string salt, int rounds = DefaultRounds)
    {
        if (rounds is < 1000 or > 999_999_999) throw new ArgumentOutOfRangeException(nameof(rounds));
        if (salt.Length > 16) salt = salt[..16];
        if (salt.Any(c => c == '$' || c == ':' || c == '\n')) throw new ArgumentException("bad salt character", nameof(salt));
        var key = Encoding.UTF8.GetBytes(password);
        var s = Encoding.UTF8.GetBytes(salt);

        var b = Sha(key, s, key);

        using var a = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        a.AppendData(key);
        a.AppendData(s);
        int cnt;
        for (cnt = key.Length; cnt > 64; cnt -= 64) a.AppendData(b);
        a.AppendData(b, 0, cnt);
        for (cnt = key.Length; cnt > 0; cnt >>= 1) a.AppendData((cnt & 1) != 0 ? b : key);
        var ar = a.GetHashAndReset();

        using var dp = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        for (var i = 0; i < key.Length; i++) dp.AppendData(key);
        var p = Repeat(dp.GetHashAndReset(), key.Length);

        using var ds = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        for (var i = 0; i < 16 + ar[0]; i++) ds.AppendData(s);
        var sb = Repeat(ds.GetHashAndReset(), s.Length);

        var c = ar;
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        for (var i = 0; i < rounds; i++)
        {
            h.AppendData((i & 1) != 0 ? p : c);
            if (i % 3 != 0) h.AppendData(sb);
            if (i % 7 != 0) h.AppendData(p);
            h.AppendData((i & 1) != 0 ? c : p);
            c = h.GetHashAndReset();
        }

        var o = new StringBuilder("$6$");
        if (rounds != DefaultRounds) o.Append("rounds=").Append(rounds).Append('$');
        o.Append(salt).Append('$');
        // byte order of the specification: groups (i, i+21, i+42) rotated through the 64 bytes
        int[,] order =
        {
            { 0, 21, 42 }, { 22, 43, 1 }, { 44, 2, 23 }, { 3, 24, 45 }, { 25, 46, 4 }, { 47, 5, 26 }, { 6, 27, 48 },
            { 28, 49, 7 }, { 50, 8, 29 }, { 9, 30, 51 }, { 31, 52, 10 }, { 53, 11, 32 }, { 12, 33, 54 }, { 34, 55, 13 },
            { 56, 14, 35 }, { 15, 36, 57 }, { 37, 58, 16 }, { 59, 17, 38 }, { 18, 39, 60 }, { 40, 61, 19 }, { 62, 20, 41 },
        };
        for (var g = 0; g < order.GetLength(0); g++) B64(o, c[order[g, 0]], c[order[g, 1]], c[order[g, 2]], 4);
        B64(o, 0, 0, c[63], 2);
        return o.ToString();
    }

    static byte[] Sha(params byte[][] parts)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        foreach (var x in parts) h.AppendData(x);
        return h.GetHashAndReset();
    }

    static byte[] Repeat(byte[] digest, int length)
    {
        var r = new byte[length];
        for (var i = 0; i < length; i++) r[i] = digest[i % digest.Length];
        return r;
    }

    static void B64(StringBuilder o, byte b2, byte b1, byte b0, int n)
    {
        var w = (b2 << 16) | (b1 << 8) | b0;
        for (var i = 0; i < n; i++) { o.Append(Itoa64[w & 0x3f]); w >>= 6; }
    }
}
