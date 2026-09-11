using System;

namespace DLSS_Swapper.Releases;

/// <summary>
/// Whether a downloaded release asset is the one GitHub says it published.
/// </summary>
/// <remarks>
/// <para>
/// GitHub's release API reports a <c>digest</c> for every asset, in the form
/// <c>sha256:&lt;64 hex characters&gt;</c>. The updater already parsed it - and used it only to
/// decide whether a file left over from an earlier attempt could be reused without downloading
/// again. A fresh download went from the network straight to Process.Start with nothing in
/// between. For an unsigned installer, that check is the only thing standing between "GitHub
/// served this" and "this ran as you".
/// </para>
/// <para>
/// Strict on purpose. An empty digest is a refusal, not a pass: the API has published one for
/// every asset for some time, so its absence means the response is not what was expected. A
/// digest naming any other algorithm is a refusal for the same reason.
/// </para>
/// </remarks>
public static class ReleaseDigest
{
    const string Prefix = "sha256:";
    const int Sha256HexLength = 64;

    /// <summary>The hex a well-formed sha256 digest carries, or false for anything else.</summary>
    public static bool TryParseSha256(string? digest, out string hex)
    {
        hex = string.Empty;

        if (string.IsNullOrWhiteSpace(digest))
        {
            return false;
        }

        var trimmed = digest.Trim();
        if (trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) == false)
        {
            return false;
        }

        var candidate = trimmed.Substring(Prefix.Length);
        if (candidate.Length != Sha256HexLength)
        {
            return false;
        }

        foreach (var c in candidate)
        {
            if (Uri.IsHexDigit(c) == false)
            {
                return false;
            }
        }

        hex = candidate;
        return true;
    }

    /// <summary>
    /// True only when <paramref name="digest"/> is a well-formed sha256 digest and its hex equals
    /// <paramref name="fileSha256Hex"/>, ignoring case.
    /// </summary>
    public static bool Matches(string? digest, string? fileSha256Hex)
    {
        if (TryParseSha256(digest, out var expected) == false)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(fileSha256Hex) || fileSha256Hex.Trim().Length != Sha256HexLength)
        {
            return false;
        }

        return string.Equals(expected, fileSha256Hex.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
