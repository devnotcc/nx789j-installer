using NxInstaller.Core;
using NxInstaller.Core.Edl;
using Xunit;

namespace NxInstaller.Core.Tests;

public class WorkDirTests
{
    [Fact]
    public void Firehose_left_running_by_an_earlier_session_is_recognised()
    {
        // QSaharaServer output when the phone is still in firehose mode (2026-11-06)
        Assert.True(EdlSession.FirehoseAlreadyRunning("WARNING: boot_usb_rx_data:195\nERROR: is_firehose:334 FIREHOSE MODE DETECTED.\nERROR: sahara_main:1140 Sahara protocol error"));
        Assert.False(EdlSession.FirehoseAlreadyRunning("ERROR: sahara_main:1140 Sahara protocol error"));
    }

    [Fact]
    public void Only_the_installed_version_stays_unpacked()
    {
        var root = AppPaths.Root;
        var tmp = Path.Combine(Path.GetTempPath(), "nxwork-" + Guid.NewGuid().ToString("N"));
        try
        {
            AppPaths.Root = tmp;
            foreach (var v in new[] { "16.0-20261104", "16.0-20261106", "16.0-20261023" })
                Directory.CreateDirectory(Path.Combine(AppPaths.Work, "evox", v));
            Directory.CreateDirectory(Path.Combine(AppPaths.Work, "debian-plasma", "13-20261104"));
            AppPaths.PruneOtherVersions("evox", "16.0-20261106");
            Assert.Equal(["16.0-20261106"], Directory.GetDirectories(Path.Combine(AppPaths.Work, "evox")).Select(Path.GetFileName));
            Assert.True(Directory.Exists(Path.Combine(AppPaths.Work, "debian-plasma", "13-20261104")));
            AppPaths.PruneOtherVersions("nothing-here", "1");   // no directory: no error
            Assert.True(AppPaths.FreeBytes(tmp) > 0);
        }
        finally
        {
            AppPaths.Root = root;
            Directory.Delete(tmp, true);
        }
    }
}
