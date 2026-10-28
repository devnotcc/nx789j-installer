using System.Text;
using NxInstaller.Core.Safety;
using Xunit;

namespace NxInstaller.Core.Tests;

public class DevInfoTests
{
    static byte[] Make(byte unlocked, byte critical)
    {
        var b = new byte[16];
        Encoding.ASCII.GetBytes("ANDROID-BOOT!").CopyTo(b, 0);
        b[13] = unlocked;
        b[14] = critical;
        return b;
    }

    [Fact]
    public void ReadsUnlocked()
    {
        // Matches the real devinfo bytes read from the phone: 41 4E 44 52 4F 49 44 2D 42 4F 4F 54 21 01 01 00
        var info = DevInfo.Parse(Make(1, 1));
        Assert.True(info.Unlocked);
        Assert.True(info.CriticalUnlocked);
    }

    [Fact]
    public void ReadsLocked()
    {
        var info = DevInfo.Parse(Make(0, 0));
        Assert.False(info.Unlocked);
    }

    [Fact]
    public void RejectsWrongMagic()
    {
        var b = new byte[16];
        Encoding.ASCII.GetBytes("NOT-ANDROID!!").CopyTo(b, 0);
        Assert.Throws<InvalidDataException>(() => DevInfo.Parse(b));
    }

    [Fact]
    public void RejectsShortData() =>
        Assert.Throws<InvalidDataException>(() => DevInfo.Parse(new byte[8]));
}
