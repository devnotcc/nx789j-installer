using System.Security.Cryptography;

namespace NxInstaller.Core.Edl;

/// <summary>
/// The Qualcomm EDL programs and the NX789J firehose loader. These are proprietary (Qualcomm and
/// ZTE) and are not redistributed with this app. The user points the app at the ZTE toolbox they
/// already have from unlocking the bootloader; this class finds the files inside it.
/// </summary>
public sealed record EdlTools(string Sahara, string FhLoader, string CmdHelper, string Firehose)
{
    /// <summary>Firehose builds this app has been tested with. Others work in principle but get a warning.</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownFirehoses = new Dictionary<string, string>
    {
        ["0aab3f764c8ffa28ae84dee5bafb26f8ad21d6ee626adb375b1a1b483316783c"] = "ZTE toolbox NX789J devprg (2025-07)",
    };

    public bool FirehoseKnown => KnownFirehoses.ContainsKey(Sha256(Firehose));

    /// <summary>Looks for the tools in a folder (the toolbox root, its bin folder, or a flat folder).</summary>
    public static EdlTools? Find(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        string? Pick(params string[] rel) =>
            rel.Select(r => Path.Combine(folder, r)).FirstOrDefault(File.Exists);

        var sahara = Pick("QSaharaServer.exe", @"tool\Win\QSaharaServer.exe", @"bin\tool\Win\QSaharaServer.exe");
        var fh = Pick("fh_loader.exe", @"tool\Win\fh_loader.exe", @"bin\tool\Win\fh_loader.exe");
        var helper = Pick("qcedlcmdhelper.exe", @"tool\Win\qcedlcmdhelper.exe", @"bin\tool\Win\qcedlcmdhelper.exe");
        var prog = Pick("devprg", @"res\NX789J\devprg", @"bin\res\NX789J\devprg",
                        "prog_firehose_ddr.elf", @"res\NX789J\prog_firehose_ddr.elf");
        return sahara is null || fh is null || helper is null || prog is null ? null : new EdlTools(sahara, fh, helper, prog);
    }

    /// <summary>Places a typical install keeps the toolbox, tried before asking the user.</summary>
    public static EdlTools? FindDefault(string? configured)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(configured)) candidates.Add(configured);
        candidates.AddRange([@"C:\zte\bin", @"C:\zte", @"C:\ztetoolbox\bin", @"C:\ZTEToolBox\bin"]);
        return candidates.Select(Find).FirstOrDefault(t => t is not null);
    }

    static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }
}
