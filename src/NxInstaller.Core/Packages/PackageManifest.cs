using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

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
    public const int CurrentFormat = 2;
    /// <summary>Format of a manifest that has <see cref="Files"/>: older installers must refuse it,
    /// since they would write the kernel and skip the files.</summary>
    public const int FilesFormat = 2;

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
    /// <summary>Files copied onto the phone's storage over adb instead of written to a partition
    /// (the Linux image and its launchers). Only names in <see cref="Linux.LinuxAgent.UploadNames"/>.</summary>
    public List<PackageFile> Files { get; init; } = [];
    /// <summary>Package ids that must be installed first or together (Debian needs an Android base).</summary>
    public List<string> Requires { get; init; } = [];
    public List<string> Notes { get; init; } = [];

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
        TypeInfoResolver = CoreJson.Default,
    };

    /// <summary>Typed metadata for T under <see cref="Json"/> (generated at compile time, trim-safe).</summary>
    internal static JsonTypeInfo<T> Info<T>() => (JsonTypeInfo<T>)Json.GetTypeInfo(typeof(T));

    public static PackageManifest Load(string json)
    {
        var m = JsonSerializer.Deserialize(json, Info<PackageManifest>()) ?? throw new InvalidDataException("empty manifest");
        m.Validate();
        return m;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Info<PackageManifest>());

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
        foreach (var f in Files)
        {
            if (!Linux.LinuxAgent.UploadNames.Contains(f.File)) throw new InvalidDataException($"{Id}: {f.File} is not a file the phone accepts");
            if (f.Sha256.Length != 64) throw new InvalidDataException($"{Id}: {f.File} has no valid sha256");
            if (f.Size <= 0) throw new InvalidDataException($"{Id}: {f.File} has no size");
        }
        if (Files.Count > 0 && Format < FilesFormat) throw new InvalidDataException($"{Id}: a manifest with files must be format {FilesFormat}");
        if (Kind == PackageKind.Linux && !Files.Any(f => f.File == Linux.LinuxAgent.Image))
            throw new InvalidDataException($"{Id}: a Linux package needs {Linux.LinuxAgent.Image}");
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

/// <summary>A file the installer uploads to the phone (see <see cref="PackageManifest.Files"/>).</summary>
public sealed record PackageFile
{
    /// <summary>Name on the phone, after joining and decompressing.</summary>
    public required string File { get; init; }
    public long Size { get; init; }
    public required string Sha256 { get; init; }
    public Compression Compression { get; init; } = Compression.None;
    public List<FilePart> Parts { get; init; } = [];
}

public enum Compression { None, Zstd }

public sealed record FilePart(string Name, long Size, string Sha256);
