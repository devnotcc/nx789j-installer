using System.Text.Json;
using NxInstaller.Core;

namespace NxInstaller.App.Services;

/// <summary>What the app remembers between runs. Never holds key material.</summary>
public sealed class AppSettings
{
    /// <summary>GitHub repository the OS packages are published in, as owner/name.</summary>
    public string Repository { get; set; } = "nx789j-installer/releases";
    /// <summary>A local folder or USB drive with packages, used instead of GitHub when set.</summary>
    public string? LocalPackages { get; set; }
    /// <summary>The ZTE toolbox folder holding the EDL tools and firehose.</summary>
    public string? EdlToolsFolder { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.Settings))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.Settings)) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(AppPaths.Settings, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
