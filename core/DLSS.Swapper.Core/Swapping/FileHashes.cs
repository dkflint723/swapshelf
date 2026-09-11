using System;
using System.IO;
using System.Security.Cryptography;

namespace DLSS_Swapper.Swapping;

/// <summary>
/// The hashes the swap executor compares files by.
/// </summary>
/// <remarks>
/// <para>
/// MD5, because that is what every game asset, backup record and manifest entry in the database
/// already carries, and a check against a recorded value has to use the algorithm the value was
/// recorded with. It is here to answer "is this file the one that was saved", not to resist a
/// forger with a budget - and for that question, against a file the app wrote itself, it is
/// enough.
/// </para>
/// <para>
/// SHA-256 is recorded beside it now - on every scanned dll, every imported record, every mirrored
/// original, and on a manifest entry from the first time its file is downloaded or swapped - and
/// compared wherever both sides carry one. Both come from a single read of the file, so keeping the
/// second costs no extra pass. MD5 stays the identity the manifest and the history are keyed by.
/// </para>
/// </remarks>
public static class FileHashes
{
    /// <summary>Both digests of the stream from its current position to the end, in one pass.</summary>
    public static FileDigests Compute(Stream stream)
    {
        using (var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5))
        using (var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                md5.AppendData(buffer, 0, read);
                sha256.AppendData(buffer, 0, read);
            }

            return new FileDigests(Convert.ToHexString(md5.GetHashAndReset()), Convert.ToHexString(sha256.GetHashAndReset()));
        }
    }

    /// <summary>Upper-case hex SHA-256 of the stream from its current position to the end.</summary>
    public static string Sha256Hex(Stream stream)
    {
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>Whether the stream's SHA-256 is <paramref name="expectedHex"/>, ignoring case and surrounding space.</summary>
    public static bool Sha256Matches(Stream stream, string expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex))
        {
            return false;
        }

        return string.Equals(Sha256Hex(stream), expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Two hex digests for the same file, ignoring case and surrounding space. False when either is missing.</summary>
    public static bool HexEquals(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Upper-case hex MD5 of the stream from its current position to the end.</summary>
    public static string Md5Hex(Stream stream)
    {
        return Convert.ToHexString(MD5.HashData(stream));
    }

    /// <summary>Whether the stream hashes to <paramref name="expectedHex"/>, ignoring case and surrounding space.</summary>
    public static bool Md5Matches(Stream stream, string expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex))
        {
            return false;
        }

        return string.Equals(Md5Hex(stream), expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The two digests of one file, upper-case hex, as <see cref="FileHashes.Compute"/> reads them.</summary>
public readonly record struct FileDigests(string Md5, string Sha256);
