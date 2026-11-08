using NxInstaller.Core.Device;
using NxInstaller.Core.Packages;
using NxInstaller.Core.Safety;
using Xunit;

namespace NxInstaller.Core.Tests;

public class DeviceGateFirmwareTests
{
    const string Fw133 = "MPSS.DE.7.0-02024.3-PAKALA_GEN_PACK-1.82720.133";
    const string Fw138Evox = "SS.DE.7.0-02024.3-PAKALA_GEN_PACK-1.82720.138";
    const string Fw138Stock = "MPSS.DE.7.0-02024.3-PAKALA_GEN_PACK-1.82720.138";

    static PackageManifest Evox() => new()
    {
        Id = "evox", Name = "Evolution X", Version = "1", Kind = PackageKind.Android, Images = [],
        Compat = new Compatibility { Basebands = [Fw133, Fw138Stock, Fw138Evox] },
    };

    static PhoneSnapshot Phone(string baseband) => new()
    {
        Mode = PhoneMode.Android, Model = "NX789J", Os = PhoneOs.EvolutionX, Battery = 80, Baseband = baseband,
    };

    [Fact]
    public void KnownFirmwarePassesWithoutSkip()
    {
        var c = DeviceGate.CheckPhone(Phone(Fw138Evox), Evox());
        Assert.Contains(c, x => x.Name == "Firmware" && x.State == CheckState.Pass);
        Assert.False(c.Blocks());
        Assert.False(c.NeedsFirmwareSkip());
    }

    [Fact]
    public void UnknownFirmwareWarnsTwiceAndNeedsSkipButDoesNotBlock()
    {
        var c = DeviceGate.CheckPhone(Phone("MPSS.DE.7.0-02024.3-PAKALA_GEN_PACK-1.82720.150"), Evox());
        Assert.False(c.Blocks());
        Assert.True(c.NeedsFirmwareSkip());
        var fw = Assert.Single(c, x => x.Name == "Firmware");
        Assert.Equal(CheckState.Warn, fw.State);
        Assert.Contains("RedMagicOS 11.0.4", fw.Detail);
        Assert.Contains("11.0.5", fw.Detail);
        var risk = Assert.Single(c, x => x.Name == DeviceGate.UntestedFirmware);
        Assert.Equal(CheckState.Warn, risk.State);
        Assert.Contains("boot loop", risk.Detail);
    }

    [Fact]
    public void TestedFirmwareNamesEachBuildOnce() =>
        Assert.Equal("RedMagicOS 11.0.4 (…1.82720.133) and 11.0.5 (…1.82720.138)",
            DeviceGate.TestedFirmware([Fw133, Fw138Stock, Fw138Evox]));

    [Fact]
    public void WrongModelStillBlocks()
    {
        var c = DeviceGate.CheckPhone(Phone(Fw133) with { Model = "NX733J" }, Evox());
        Assert.True(c.Blocks());
    }
}
