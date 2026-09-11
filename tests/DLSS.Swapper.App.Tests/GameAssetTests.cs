using DLSS_Swapper.Data;
using Xunit;

namespace DLSS_Swapper.App.Tests;

/// <summary>
/// Covers when a cached dll can be trusted instead of re-hashing it. Getting this wrong either
/// re-hashes gigabytes on every refresh or reuses a hash for a file that has since changed.
/// </summary>
public class GameAssetTests
{
    static GameAsset Asset(string version, long size, string hash)
    {
        return new GameAsset()
        {
            Id = "asset_1",
            AssetType = GameAssetType.DLSS,
            Path = @"C:\game\nvngx_dlss.dll",
            Version = version,
            Size = size,
            Hash = hash,
        };
    }

    [Fact]
    public void SameVersionAndSizeMatchesTheCache()
    {
        var onDisk = Asset("310.7.0.0", 1024, string.Empty);
        var cached = Asset("310.7.0.0", 1024, "ABC");
        cached.Sha256 = "DEF";

        Assert.True(onDisk.MatchesCachedFile(cached));
    }

    [Fact]
    public void ACachedRowWithoutASha256IsReadAgain()
    {
        // Rows from before the second digest was kept are re-read once, so every row ends up with both.
        var onDisk = Asset("310.7.0.0", 1024, string.Empty);
        var cached = Asset("310.7.0.0", 1024, "ABC");

        Assert.False(onDisk.MatchesCachedFile(cached));
    }

    [Fact]
    public void ADifferentVersionDoesNotMatch()
    {
        var onDisk = Asset("310.7.0.0", 1024, string.Empty);
        var cached = Asset("310.1.0.0", 1024, "ABC");

        Assert.False(onDisk.MatchesCachedFile(cached));
    }

    [Fact]
    public void ADifferentSizeDoesNotMatch()
    {
        // Same version but a different size means the file was replaced by something the version
        // resource cannot distinguish, so it has to be re-read.
        var onDisk = Asset("310.7.0.0", 2048, string.Empty);
        var cached = Asset("310.7.0.0", 1024, "ABC");

        Assert.False(onDisk.MatchesCachedFile(cached));
    }

    [Fact]
    public void ACacheEntryWithoutAHashIsUseless()
    {
        // Matching would skip the hash read and leave the asset with no hash at all, which is what
        // the match exists to avoid.
        var onDisk = Asset("310.7.0.0", 1024, string.Empty);
        var cached = Asset("310.7.0.0", 1024, string.Empty);

        Assert.False(onDisk.MatchesCachedFile(cached));
    }

    [Fact]
    public void ACacheEntryFromBeforeSizeWasRecordedIsUseless()
    {
        // Rows written before the size column existed have size 0. They cannot be matched on, so
        // they get re-read once and then carry a size.
        var onDisk = Asset("310.7.0.0", 0, string.Empty);
        var cached = Asset("310.7.0.0", 0, "ABC");

        Assert.False(onDisk.MatchesCachedFile(cached));
    }
}
