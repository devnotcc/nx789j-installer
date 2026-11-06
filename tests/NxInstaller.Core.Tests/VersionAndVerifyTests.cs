using NxInstaller.Core.Install;
using NxInstaller.Core.Packages;
using Xunit;

namespace NxInstaller.Core.Tests;

public class VersionAndVerifyTests
{
    [Theory]
    [InlineData("16.0-20261106.2", "16.0-20261106")]
    [InlineData("16.0-20261106", "16.0-20261029")]
    [InlineData("10", "9")]
    [InlineData("23.2-20261104.2", "23.2-20261104.1")]
    [InlineData("13-20261106", "13-20261103")]
    [InlineData("2026-09-25", "2026-09-03")]
    public void Newer_version_compares_greater(string newer, string older)
    {
        Assert.True(PackageVersions.Compare(newer, older) > 0);
        Assert.True(PackageVersions.Compare(older, newer) < 0);
        Assert.Equal(0, PackageVersions.Compare(newer, newer));
    }

    [Fact]
    public void Read_back_pieces_cover_the_partition_exactly()
    {
        var super = 16UL * 1024 * 1024 * 1024 / 4096;
        var pieces = Installer.VerifyChunks(super).ToList();
        Assert.Equal(16, pieces.Count);
        Assert.All(pieces, p => Assert.Equal(Installer.VerifyChunkSectors, p.Sectors));

        var odd = Installer.VerifyChunks(10, 4).ToList();
        Assert.Equal([(0UL, 4UL), (4UL, 4UL), (8UL, 2UL)], odd);
        Assert.Single(Installer.VerifyChunks(16)); // vbmeta: one small piece
        Assert.Empty(Installer.VerifyChunks(0));
    }
}
