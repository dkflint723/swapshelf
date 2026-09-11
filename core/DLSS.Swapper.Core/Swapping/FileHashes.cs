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
/// enough. When SHA-256 is recorded alongside, this is where the comparison learns about it.
/// </para>
/// </remarks>
public static class FileHashes
{
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
