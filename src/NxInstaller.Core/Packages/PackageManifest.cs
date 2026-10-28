using System.Text.Json;
using System.Text.Json.Serialization;

namespace NxInstaller.Core.Packages;

public enum PackageKind { Android, Linux, Recovery }

/// <summary>Which copy of an A/B partition an image goes to.</summary>
public enum SlotTarget
{
    /// <summary>Single partition (super, metadata, userdata).</summary>
    None,
    /// <summary>The slot the phone currently boots from (read from the GPT, never switched).</summary>
    Active,
}

/// <summary>
/// manifest.json of one installable package (an OS build, TWRP, ...). Published as an asset of a
/// GitHub release next to the image files. The installer trusts nothing in it on its own: the
/// partitions it names still go through PartitionPolicy, and every file must match its SHA-256.
/// </summary>
public sealed record PackageManifest
{
    public const int CurrentFormat = 1;

    public int Format { get; init; } = CurrentFormat;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required PackageKind Kind { get; init; }
    public string Description { get; init; } = "";
    /// <summary>False shows the package greyed out with <see cref="DisabledReason"/>.</summary>
    public bool Enabled { get; init; } = true;
    public string? DisabledReason { get; init; }
    /// <summary>Choices shown on the OS page, e.g. LineageOS with or without Google apps.</summary>
    public string? Variant { get; init; }
    public string? Family { get; init; }

    public required Compatibility Compat { get; init; }
    /// <summary>When the data partition has to be formatted (different encryption keys or OS).</summary>
    public WipePolicy Wipe { get; init; } = new();
    public required List<PackageImage> Images { get; init; }
    /// <summary>Package ids that must be installed first or together (Debian needs an Android base).</summary>
    public List<string> Requires { get; init; } = [];
    public List<string> Notes { get; init; } = [];

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    public static PackageManifest Load(string json)
    {
        var m = JsonSerializer.Deserialize<PackageManifest>(json, Json) ?? throw new InvalidDataException("empty manifest");
        m.Validate();
        return m;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public void Validate()
    {
        if (Format > CurrentFormat) throw new InvalidDataException($"{Id}: manifest format {Format} needs a newer installer");
        if (Compat.Models.Count == 0) throw new InvalidDataException($"{Id}: no compatible models listed");
        foreach (var i in Images)
        {
            if (i.Sha256.Length != 64) throw new InvalidDataException($"{Id}: {i.File} has no valid sha256");
            if (i.Size <= 0) throw new InvalidDataException($"{Id}: {i.File} has no size");
            if (i.Parts.Count > 0 && i.Parts.Any(p => p.Sha256.Length != 64))
                throw new InvalidDataException($"{Id}: a part of {i.File} has no valid sha256");
            if (Safety.PartitionPolicy.BaseName(i.Partition) != i.Partition)
                throw new InvalidDataException($"{Id}: name the partition without a slot suffix ({i.Partition}); use slot: active");
        }
    }
}

public sealed record Compatibility
{
    public List<string> Models { get; init; } = ["NX789J"];
    /// <summary>Modem firmware builds the images were tested on. Empty = any.</summary>
    public List<string> Basebands { get; init; } = [];
}

public sealed record WipePolicy
{
    /// <summary>Always format data (e.g. first install of this OS).</summary>
    public bool Always { get; init; }
    /// <summary>Format unless the phone already runs one of these package families.</summary>
    public List<string> UnlessFamily { get; init; } = [];
}

public sealed record PackageImage
{
    public required string Partition { get; init; }
    public SlotTarget Slot { get; init; } = SlotTarget.Active;
    /// <summary>Image name after joining the parts and decompressing.</summary>
    public required string File { get; init; }
    /// <summary>Uncompressed size in bytes; must be a multiple of 4096.</summary>
    public long Size { get; init; }
    /// <summary>SHA-256 of the uncompressed image.</summary>
    public required string Sha256 { get; init; }
    public Compression Compression { get; init; } = Compression.None;
    /// <summary>Release assets that make up the (compressed) file, in order. One part = not split.</summary>
    public List<FilePart> Parts { get; init; } = [];
    /// <summary>Optional images (TWRP) are only written when the user picks them.</summary>
    public string? Option { get; init; }
}

public enum Compression { None, Zstd }

public sealed record FilePart(string Name, long Size, string Sha256);
