using NxInstaller.Core.Safety;
using Xunit;

namespace NxInstaller.Core.Tests;

public class PartitionPolicyTests
{
    [Theory]
    [InlineData("super")]
    [InlineData("boot_a")]
    [InlineData("boot_b")]
    [InlineData("init_boot_a")]
    [InlineData("vendor_boot_a")]
    [InlineData("recovery_a")]
    [InlineData("vbmeta_a")]
    [InlineData("vbmeta_system_a")]
    [InlineData("dtbo_a")]
    public void AllowsOsImagePartitions(string label) =>
        Assert.True(PartitionPolicy.IsAllowed(label, WriteKind.Image, out _));

    [Theory]
    [InlineData("abl_a")]        // bootloader, carries the unlock
    [InlineData("xbl_a")]
    [InlineData("tz_a")]
    [InlineData("hyp_a")]
    [InlineData("modem_a")]
    [InlineData("dsp_a")]
    [InlineData("devcfg_a")]
    [InlineData("devinfo")]      // unlock state
    [InlineData("frp")]
    [InlineData("persist")]      // calibration
    [InlineData("modemst1")]
    [InlineData("keymaster_a")]
    [InlineData("secdata")]
    public void RefusesFirmwareAndStatePartitions(string label)
    {
        Assert.False(PartitionPolicy.IsAllowed(label, WriteKind.Image, out var reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void FirmwareRefusedEvenForWipe() =>
        Assert.False(PartitionPolicy.IsAllowed("modem_a", WriteKind.Wipe, out _));

    [Fact]
    public void SuperCannotBeWiped() =>
        Assert.False(PartitionPolicy.IsAllowed("super", WriteKind.Wipe, out _));

    [Theory]
    [InlineData("metadata")]
    [InlineData("userdata")]
    public void AllowsDataWipe(string label) =>
        Assert.True(PartitionPolicy.IsAllowed(label, WriteKind.Wipe, out _));

    [Fact]
    public void DataPartitionsAreNotImageTargets() =>
        Assert.False(PartitionPolicy.IsAllowed("userdata", WriteKind.Image, out _));

    [Fact]
    public void RequireThrowsOnForbidden() =>
        Assert.Throws<PolicyViolationException>(() => PartitionPolicy.Require("tz_a", WriteKind.Image));

    [Theory]
    [InlineData("boot_a", "boot")]
    [InlineData("boot_b", "boot")]
    [InlineData("super", "super")]
    [InlineData("vbmeta_system_a", "vbmeta_system")]
    public void BaseNameStripsSlot(string label, string expected) =>
        Assert.Equal(expected, PartitionPolicy.BaseName(label));
}
