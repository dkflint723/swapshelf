using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using DLSS_Swapper.Swapping;
using Xunit;

namespace DLSS_Swapper.Tests;

/// <summary>
/// The two digests of a file come from one read and agree with the standard algorithms.
/// </summary>
public class FileHashesTests
{
    static readonly byte[] Sample = Encoding.UTF8.GetBytes("a dll is a very large file but this will do for a hash");

    [Fact]
    public void ComputeAgreesWithEachAlgorithmOnItsOwn()
    {
        FileDigests digests;
        using (var stream = new MemoryStream(Sample))
        {
            digests = FileHashes.Compute(stream);
        }

        Assert.Equal(Convert.ToHexString(MD5.HashData(Sample)), digests.Md5);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Sample)), digests.Sha256);

        using (var stream = new MemoryStream(Sample))
        {
            Assert.Equal(digests.Md5, FileHashes.Md5Hex(stream));
        }
        using (var stream = new MemoryStream(Sample))
        {
            Assert.Equal(digests.Sha256, FileHashes.Sha256Hex(stream));
        }
    }

    [Fact]
    public void ComputeReadsFromTheCurrentPositionLikeTheOthers()
    {
        using (var stream = new MemoryStream(Sample))
        {
            stream.Position = 10;
            var digests = FileHashes.Compute(stream);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Sample.AsSpan(10))), digests.Sha256);
        }
    }

    [Fact]
    public void AnEmptyStreamStillHasDigests()
    {
        using (var stream = new MemoryStream())
        {
            var digests = FileHashes.Compute(stream);
            Assert.Equal("D41D8CD98F00B204E9800998ECF8427E", digests.Md5);
            Assert.Equal("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", digests.Sha256);
        }
    }

    [Fact]
    public void Sha256MatchesIgnoresCaseAndSpaceAndRefusesNothing()
    {
        var expected = Convert.ToHexString(SHA256.HashData(Sample));

        using (var stream = new MemoryStream(Sample))
        {
            Assert.True(FileHashes.Sha256Matches(stream, " " + expected.ToLowerInvariant() + " "));
        }
        using (var stream = new MemoryStream(Sample))
        {
            Assert.False(FileHashes.Sha256Matches(stream, ""));
        }
        using (var stream = new MemoryStream(Sample))
        {
            Assert.False(FileHashes.Sha256Matches(stream, expected.Substring(1)));
        }
    }

    [Fact]
    public void HexEqualsIsForgivingAboutFormAndStrictAboutAbsence()
    {
        Assert.True(FileHashes.HexEquals("abc123", " ABC123 "));
        Assert.False(FileHashes.HexEquals("abc123", "abc124"));
        Assert.False(FileHashes.HexEquals("", "abc123"));
        Assert.False(FileHashes.HexEquals("abc123", null));
        Assert.False(FileHashes.HexEquals(null, null));
    }
}
