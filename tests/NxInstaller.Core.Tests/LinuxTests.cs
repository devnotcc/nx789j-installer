using NxInstaller.Core.Device;
using NxInstaller.Core.Linux;
using NxInstaller.Core.Packages;
using NxInstaller.Core.Safety;
using Xunit;

namespace NxInstaller.Core.Tests;

public class LinuxTests
{
    [Theory]
    // first: the specification's own test vector; the others from "openssl passwd -6"
    [InlineData("Hello world!", "saltstring", "$6$saltstring$svn8UoSVapNtMuq1ukKS4tPQd8iKwSMHWjl/O817G3uBnIFNjnQJuesI68u4OTLiBFdcbYEdFCoEOfaS35inz1")]
    [InlineData("pässwörd with spaces", "abcDEF0123./xyz9", "$6$abcDEF0123./xyz9$Ck15DMnwyAMt/xOwV9e6KqU2tPXKCWQ1uB/MUlQ8tKDkigpWr01E2SsUuzGuT7SFEPbFcB8xPx9J1Wbi.y8sD0")]
    public void Sha512CryptMatchesReference(string password, string salt, string expected) =>
        Assert.Equal(expected, Sha512Crypt.Hash(password, salt));

    [Fact]
    public void Sha512CryptSpecRoundsVector() =>
        // specification: rounds=10000, "saltstringsaltstring" is cut to 16 characters
        Assert.Equal("$6$rounds=10000$saltstringsaltst$OW1/O6BYHV6BcXZu8QVeXbDWra3Oeqh0sbHbbMCVNSnCM/UrjmM0Dp8vOuZeHBy/YTBmSK6H9qs/y3RnOaw5v.",
            Sha512Crypt.Hash("Hello world!", "saltstringsaltstring", 10000));

    [Fact]
    public void RandomSaltHashesDifferAndHaveTheShadowShape()
    {
        var a = Sha512Crypt.Hash("secret");
        var b = Sha512Crypt.Hash("secret");
        Assert.NotEqual(a, b);
        Assert.Matches(@"^\$6\$[./A-Za-z0-9]{16}\$[./A-Za-z0-9]{86}$", a);
    }

    [Fact]
    public void ManifestHasTheAgentsFormat()
    {
        var sha = new string('a', 64);
        var text = LinuxAgent.Manifest([new("rootfs.ext4", 8192, sha), new("firstboot.conf", 107, sha)], 64L << 30, replaceImage: true);
        Assert.Equal($"nx-install 1\nfile rootfs.ext4 8192 {sha}\nfile firstboot.conf 107 {sha}\ngrow rootfs.ext4 {64L << 30}\nreplace rootfs.ext4\n", text);
    }

    [Theory]
    [InlineData("evil.sh")]
    [InlineData("../rootfs.ext4")]
    [InlineData("busybox-arm64")]
    public void ManifestRefusesNamesTheAgentRefuses(string name) =>
        Assert.Throws<ArgumentException>(() => LinuxAgent.Manifest([new(name, 1, new string('0', 64))]));

    [Fact]
    public void ManifestRefusesBadHashAndOddGrowSize()
    {
        Assert.Throws<ArgumentException>(() => LinuxAgent.Manifest([new("bootmode", 6, new string('A', 64))]));
        Assert.Throws<ArgumentException>(() => LinuxAgent.Manifest([], 4097));
    }

    [Fact]
    public void ParsesStatus()
    {
        Assert.Null(LinuxAgent.ParseStatus(""));
        var ok = LinuxAgent.ParseStatus("ok\r\ninstalled rootfs.ext4\nrootfs.ext4 size 34359738368\n")!;
        Assert.True(ok.Ok);
        Assert.Equal(2, ok.Details.Count);
        var err = LinuxAgent.ParseStatus("error rootfs.ext4 does not match its sha256\n")!;
        Assert.False(err.Ok);
        Assert.Equal("rootfs.ext4 does not match its sha256", err.Reason);
    }

    [Fact]
    public void ParsesToyboxDf() =>
        Assert.Equal(118_000_000L * 1024, LinuxAgent.ParseDfAvailable(
            "Filesystem       1K-blocks     Used Available Use% Mounted on\n/dev/block/dm-52 230000000 112000000 118000000  49% /data\n"));

    [Fact]
    public void FirstbootNeedsASha512Hash()
    {
        Assert.Equal("password_hash=$6$s$h\n", LinuxAgent.FirstbootFor("$6$s$h"));
        Assert.Throws<ArgumentException>(() => LinuxAgent.FirstbootFor("plaintext"));
        Assert.Throws<ArgumentException>(() => LinuxAgent.FirstbootFor("$6$s$h\nroot::0:0"));
    }

    static PackageManifest LinuxManifest(List<string>? requires = null) => new()
    {
        Format = PackageManifest.FilesFormat, Id = "debian-plasma", Name = "Debian", Version = "1", Kind = PackageKind.Linux,
        Compat = new(), Images = [], Requires = requires ?? ["evox"],
        Files = [new PackageFile { File = "rootfs.ext4", Size = 4096, Sha256 = new string('0', 64) }],
    };

    [Fact]
    public void LinuxNeedsItsAndroidBase()
    {
        var m = LinuxManifest();
        Assert.Null(LinuxInstaller.CheckBase(m, "evolutionx"));
        Assert.NotNull(LinuxInstaller.CheckBase(m, "lineageos"));
        Assert.NotNull(LinuxInstaller.CheckBase(m, null));
        Assert.Null(LinuxInstaller.CheckBase(LinuxManifest(["evox", "lineage-gapps"]), "lineageos"));
    }

    [Fact]
    public void ManifestValidationOfFiles()
    {
        LinuxManifest().Validate();
        Assert.Throws<InvalidDataException>(() => (LinuxManifest() with { Format = 1 }).Validate());
        Assert.Throws<InvalidDataException>(() => (LinuxManifest() with { Files = [new PackageFile { File = "evil.sh", Size = 1, Sha256 = new string('0', 64) }] }).Validate());
        Assert.Throws<InvalidDataException>(() => (LinuxManifest() with { Files = [] }).Validate());
        // an installer that only knows format 1 must refuse the package instead of half-installing it
        var json = LinuxManifest().ToJson().Replace("\"format\": 2", "\"format\": 3");
        Assert.Throws<InvalidDataException>(() => PackageManifest.Load(json));
    }

    [Fact]
    public void GateWantsAndroidForLinux()
    {
        var phone = new PhoneSnapshot { Mode = PhoneMode.Recovery, Model = "NX789J", Battery = 80, Os = PhoneOs.Twrp };
        Assert.Contains(DeviceGate.CheckPhone(phone, LinuxManifest()), c => c.Name == "Android" && c.State == CheckState.Fail);
        var evox = phone with { Mode = PhoneMode.Android, Os = PhoneOs.EvolutionX };
        Assert.Contains(DeviceGate.CheckPhone(evox, LinuxManifest()), c => c.Name == "Android" && c.State == CheckState.Pass);
    }

    [Fact]
    public async Task PackageWithFilesRoundTrips()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nx-pack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var raw = new byte[40 * 1024];
            Random.Shared.NextBytes(raw);
            var img = Path.Combine(root, "init_boot.img");
            await File.WriteAllBytesAsync(img, new byte[8192]);
            var fs = Path.Combine(root, "root.img");
            await File.WriteAllBytesAsync(fs, raw);
            var spec = new PackageSpec
            {
                Id = "debian-plasma", Name = "Debian", Version = "1", Kind = PackageKind.Linux, Requires = ["evox"],
                Images = [new SpecImage("init_boot", SlotTarget.Active, img)],
                Files = [new SpecFile("rootfs.ext4", fs)],
            };
            using var log = new Log(Path.Combine(root, "logs"));
            var outDir = Path.Combine(root, "pkg");
            var m = await PackageBuilder.BuildAsync(spec, outDir, log, level: 1, partSize: 16 * 1024);
            Assert.Equal(PackageManifest.FilesFormat, m.Format);
            Assert.True(Assert.Single(m.Files).Parts.Count > 1);

            var entry = Assert.Single(await new FolderSource(outDir).ListAsync(default));
            var built = await ImageAssembler.AssembleAsync(entry, entry.Manifest.Files[0], Path.Combine(root, "dl"), Path.Combine(root, "work"), log, null, default);
            Assert.Equal("rootfs.ext4", Path.GetFileName(built));
            Assert.Equal(raw, await File.ReadAllBytesAsync(built));
        }
        finally { Directory.Delete(root, true); }
    }
}
