using System.Text.Json.Serialization;
using NxInstaller.Core.Backup;
using NxInstaller.Core.Packages;

namespace NxInstaller.Core;

/// <summary>
/// Compile-time JSON code for every type the engine reads or writes. The release app is trimmed,
/// and reflection-based JSON could lose the members it needs; with this the serializer never
/// looks types up at run time. Naming and enum options come from <see cref="PackageManifest.Json"/>.
/// </summary>
[JsonSerializable(typeof(PackageManifest))]
[JsonSerializable(typeof(PackageSpec))]
[JsonSerializable(typeof(BackupManifest))]
[JsonSerializable(typeof(List<GhRelease>))]
internal sealed partial class CoreJson : JsonSerializerContext;

/// <summary>The bits of the GitHub releases API the package source reads.</summary>
internal sealed record GhRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("assets")] List<GhAsset> Assets);

internal sealed record GhAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);
