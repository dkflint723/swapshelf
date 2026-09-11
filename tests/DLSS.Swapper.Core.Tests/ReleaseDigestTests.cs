using DLSS_Swapper.Releases;
using Xunit;

namespace DLSS_Swapper.Tests;

/// <summary>
/// The check between "GitHub served this installer" and "this installer ran".
/// </summary>
/// <remarks>
/// Every case here is one the updater used to wave through, because it never asked. The digest was
/// only ever consulted to decide whether a file from an earlier attempt could be reused.
/// </remarks>
public class ReleaseDigestTests
{
    const string Hex = "18992ab6a3d9c1b721f6f02ea542fec45924f5009f5011f0f450d70385038f08";

    [Fact]
    public void TheDigestGitHubPublishesMatchesTheFileItDescribes()
    {
        Assert.True(ReleaseDigest.Matches("sha256:" + Hex, Hex));
    }

    [Fact]
    public void CaseDoesNotMatter()
    {
        // GitHub emits lower case; the hashing helper here emits upper case. Neither should have to
        // know about the other.
        Assert.True(ReleaseDigest.Matches("sha256:" + Hex, Hex.ToUpperInvariant()));
        Assert.True(ReleaseDigest.Matches("SHA256:" + Hex.ToUpperInvariant(), Hex));
    }

    [Fact]
    public void OneDifferentCharacterIsADifferentFile()
    {
        var altered = "0" + Hex.Substring(1);

        Assert.False(ReleaseDigest.Matches("sha256:" + Hex, altered));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoDigestIsARefusalNotAPass(string? digest)
    {
        // The API publishes one for every asset. Its absence means the response is not the one
        // expected, and the safe reading of "I cannot check" is "no".
        Assert.False(ReleaseDigest.Matches(digest, Hex));
    }

    [Theory]
    [InlineData("sha1:" + Hex)]
    [InlineData("md5:" + Hex)]
    [InlineData(Hex)]
    public void OnlySha256IsAccepted(string digest)
    {
        Assert.False(ReleaseDigest.Matches(digest, Hex));
    }

    [Theory]
    [InlineData("sha256:abc")]
    [InlineData("sha256:" + Hex + "00")]
    [InlineData("sha256:" + "zz" + "992ab6a3d9c1b721f6f02ea542fec45924f5009f5011f0f450d70385038f")]
    public void AMalformedDigestIsARefusal(string digest)
    {
        Assert.False(ReleaseDigest.TryParseSha256(digest, out _));
        Assert.False(ReleaseDigest.Matches(digest, Hex));
    }

    [Fact]
    public void AMissingOrShortFileHashNeverMatches()
    {
        Assert.False(ReleaseDigest.Matches("sha256:" + Hex, null));
        Assert.False(ReleaseDigest.Matches("sha256:" + Hex, ""));
        Assert.False(ReleaseDigest.Matches("sha256:" + Hex, Hex.Substring(0, 63)));
    }

    [Fact]
    public void ParsingHandsBackTheHex()
    {
        Assert.True(ReleaseDigest.TryParseSha256("  sha256:" + Hex + "  ", out var hex));
        Assert.Equal(Hex, hex);
    }
}
