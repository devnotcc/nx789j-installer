using System.Text;
using NxInstaller.Core.Device;
using NxInstaller.Core.Install;
using NxInstaller.Core.Packages;
using Xunit;

namespace NxInstaller.Core.Tests;

public class PostInstallCheckTests
{
    const string Banner = "Linux version 6.6.92-android15-8-g3637f4904cf5-4k (build@build) (Android (11368308, +pgo, +bolt, +lto, +mlgo, based on r510928) clang version 18.0.0, LLD 18.0.0) #1 SMP PREEMPT Mon Nov  3 17:02:11 UTC 2026";

    static string BootImage(byte[] kernel)
    {
        var path = Path.GetTempFileName();
        var img = new byte[4096 + kernel.Length + 4096];
        Encoding.ASCII.GetBytes("ANDROID!").CopyTo(img, 0);
        BitConverter.GetBytes(kernel.Length).CopyTo(img, 8);
        BitConverter.GetBytes(4).CopyTo(img, 40); // header version 4
        kernel.CopyTo(img, 4096);
        File.WriteAllBytes(path, img);
        return path;
    }

    [Fact]
    public void KernelBanner_is_read_from_the_boot_image()
    {
        var kernel = new byte[100_000];
        var b = Encoding.ASCII.GetBytes(Banner + "\n\0");
        b.CopyTo(kernel, 54_321);
        var p = BootImage(kernel);
        try { Assert.Equal(Banner, PostInstallCheck.KernelBannerOf(p)); }
        finally { File.Delete(p); }
    }

    [Fact]
    public void KernelBanner_is_null_for_a_compressed_kernel_or_another_file()
    {
        var p = BootImage(new byte[50_000]);
        var q = Path.GetTempFileName();
        File.WriteAllBytes(q, new byte[8192]);
        try
        {
            Assert.Null(PostInstallCheck.KernelBannerOf(p));
            Assert.Null(PostInstallCheck.KernelBannerOf(q));
        }
        finally { File.Delete(p); File.Delete(q); }
    }

    [Fact]
    public void KernelRelease_is_the_third_word()
    {
        Assert.Equal("6.6.92-android15-8-g3637f4904cf5-4k", PostInstallCheck.KernelRelease(Banner));
        Assert.Equal("something else", PostInstallCheck.KernelRelease("something else"));
    }

    [Fact]
    public void Banner_id_ignores_the_build_number_and_date()
    {
        // as in boot_susfs7_linux.img (tail unfilled) vs /proc/version on the phone (2026-11-04)
        const string image = "Linux version 6.6.92-4k-g7548e17901c5 (build@build) (Android (11368308, +pgo) clang version 18.0.0, LLD 18.0.0) # SMP PREEMPT";
        const string running = "Linux version 6.6.92-4k-g7548e17901c5 (build@build) (Android (11368308, +pgo) clang version 18.0.0, LLD 18.0.0) #8 SMP PREEMPT Tue Nov  3 19:12:11 EET 2026";
        Assert.Equal(PostInstallCheck.BannerId(image), PostInstallCheck.BannerId(running));
        Assert.NotEqual(PostInstallCheck.BannerId(Banner), PostInstallCheck.BannerId(running));
    }

    [Fact]
    public void Families_match_the_package_families()
    {
        Assert.Equal("evolutionx", PostInstallCheck.FamilyOf(PhoneOs.EvolutionX));
        Assert.Equal("lineageos", PostInstallCheck.FamilyOf(PhoneOs.LineageOS));
        Assert.Null(PostInstallCheck.FamilyOf(PhoneOs.Stock));
    }

    [Fact]
    public void ExpectProps_survive_the_manifest_and_default_to_empty()
    {
        var m = new PackageManifest
        {
            Id = "evox", Name = "Evolution X", Version = "1", Kind = PackageKind.Android, Compat = new(), Images = [],
            ExpectProps = new() { ["ro.build.date.utc"] = "1762214400" },
        };
        var back = PackageManifest.Load(m.ToJson());
        Assert.Equal("1762214400", back.ExpectProps["ro.build.date.utc"]);

        var old = PackageManifest.Load("""{"id":"x","name":"x","version":"1","kind":"android","compat":{"models":["NX789J"]},"images":[]}""");
        Assert.Empty(old.ExpectProps);
    }

    [Fact]
    public void Linux_state_command_reads_systemd_inside_its_namespaces()
    {
        var c = PostInstallCheck.LinuxStateCommand;
        Assert.StartsWith("B=/data/nx-linux; p=$(cat $B/systemd.pid", c);
        Assert.Contains("nsenter -t $p -m -u -i -p -n systemctl is-system-running", c);
        Assert.Contains("echo no-systemd", c);
    }
}
