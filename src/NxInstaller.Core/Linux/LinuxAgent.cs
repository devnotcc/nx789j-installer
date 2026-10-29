using System.Text;

namespace NxInstaller.Core.Linux;

/// <summary>Result of one upload, from nx-install/STATUS.</summary>
public sealed record AgentStatus(bool Ok, string Reason, IReadOnlyList<string> Details);

/// <summary>
/// The protocol of the install agent in the public kernel-2 init_boot (tools/linux/initboot/
/// nx-agent.sh): the installer uploads files to <see cref="RemoteDir"/> with adb, then a manifest,
/// then READY. On the next boot the agent checks every file (size, SHA-256, allowed name) and
/// moves them into /data/nx-linux as root, and writes STATUS. Keep both sides in step.
/// </summary>
public static class LinuxAgent
{
    public const string RemoteDir = "/data/local/tmp/nx-install";
    public const string InstallDir = "/data/nx-linux";
    public const string Image = "rootfs.ext4";
    public const string FirstbootConf = "firstboot.conf";
    public const string BootMode = "bootmode";

    /// <summary>Files a package may carry.</summary>
    public static readonly IReadOnlySet<string> UploadNames = new HashSet<string>(StringComparer.Ordinal)
    {
        Image, "nx-linux.sh", "nx-linux-boot.sh", "nx-systemd.sh", "backend_conf.xml",
    };

    /// <summary>Everything the agent accepts: the package files plus the two the installer writes itself.</summary>
    public static readonly IReadOnlySet<string> AgentNames = new HashSet<string>(UploadNames, StringComparer.Ordinal) { FirstbootConf, BootMode };

    /// <summary>Space the agent leaves free on /data when growing the image (it refuses beyond that).</summary>
    public const long AgentReserve = 2L << 30;

    public sealed record Upload(string Name, long Size, string Sha256);

    public static string Manifest(IEnumerable<Upload> files, long? growImageTo = null, bool replaceImage = false)
    {
        var sb = new StringBuilder("nx-install 1\n");
        var seen = new HashSet<string>();
        foreach (var f in files)
        {
            if (!AgentNames.Contains(f.Name)) throw new ArgumentException($"{f.Name} is not a file the phone accepts");
            if (!seen.Add(f.Name)) throw new ArgumentException($"{f.Name} listed twice");
            if (f.Sha256.Length != 64 || f.Sha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new ArgumentException($"{f.Name}: sha256 must be 64 lowercase hex digits");
            sb.Append($"file {f.Name} {f.Size} {f.Sha256}\n");
        }
        if (growImageTo is { } g)
        {
            if (g % 4096 != 0) throw new ArgumentException("image size must be a multiple of 4096");
            sb.Append($"grow {Image} {g}\n");
        }
        if (replaceImage) sb.Append($"replace {Image}\n");
        return sb.ToString();
    }

    /// <summary>The contents of firstboot.conf for a SHA-512 crypt hash (see <see cref="Sha512Crypt"/>).</summary>
    public static string FirstbootFor(string passwordHash)
    {
        if (!passwordHash.StartsWith("$6$", StringComparison.Ordinal) || passwordHash.Any(char.IsWhiteSpace))
            throw new ArgumentException("not a SHA-512 crypt hash", nameof(passwordHash));
        return $"password_hash={passwordHash}\n";
    }

    /// <summary>Parses STATUS; null when the text is empty (the agent has not run yet).</summary>
    public static AgentStatus? ParseStatus(string? text)
    {
        var lines = (text ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;
        var first = lines[0].Trim();
        if (first == "ok") return new AgentStatus(true, "", lines[1..]);
        var reason = first.StartsWith("error ", StringComparison.Ordinal) ? first[6..] : $"unexpected status: {first}";
        return new AgentStatus(false, reason, lines[1..]);
    }

    /// <summary>Available bytes on /data from toybox "df -k /data" output, or null.</summary>
    public static long? ParseDfAvailable(string dfOutput)
    {
        var line = dfOutput.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        var f = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return f is { Length: >= 4 } && long.TryParse(f[3], out var kb) ? kb * 1024 : null;
    }
}
