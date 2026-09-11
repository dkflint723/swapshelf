using System;
using System.Collections.Generic;
using DLSS_Swapper.Data;

namespace DLSS_Swapper.Signing;

/// <summary>
/// Which certificate subjects are the vendor for each kind of dll.
/// </summary>
/// <remarks>
/// <para>
/// A signature that chains to a trusted root proves the file has not changed since somebody
/// signed it. It says nothing about who. Until this list existed a dll named nvngx_dlss.dll and
/// validly signed by any company on earth read as trusted, which made the check a test of
/// whether the author had bought a certificate rather than of whether NVIDIA had built the file.
/// </para>
/// <para>
/// The names are measured, not guessed: every dll in a library of 26 across all ten types was read
/// with Get-AuthenticodeSignature, and these are the subject names that came back. AMD, for
/// instance, signs as "Advanced Micro Devices" with no ", Inc." - a guess would have refused every
/// FSR dll. The one variant per vendor marked below as not observed is included because it is that
/// vendor's other well-known signing name, and refusing a genuine file is the failure this list
/// must not have. If a genuine dll is ever refused, the message names the subject it saw, and the
/// fix is one line here.
/// </para>
/// </remarks>
public static class PublisherAllowList
{
    static readonly IReadOnlyDictionary<DllVendor, string[]> _accepted = new Dictionary<DllVendor, string[]>()
    {
        [DllVendor.Nvidia] = new[]
        {
            "NVIDIA Corporation",             // observed on dlss, dlss_g, dlss_d, dlss_nr
        },
        [DllVendor.Amd] = new[]
        {
            "Advanced Micro Devices",         // observed on fsr_31_dx12, fsr_31_vk
            "Advanced Micro Devices, Inc.",   // the legal name; not observed, kept so a future build signed with it is not refused
        },
        [DllVendor.Intel] = new[]
        {
            "Intel Corporation",              // observed on xess, xess_fg, xess_dx11, xell
            "Intel(R) Corporation",           // Intel's other signing name; not observed here
        },
    };

    /// <summary>
    /// Whether <paramref name="publisher"/> - a certificate's simple subject name - is the vendor of
    /// <paramref name="vendor"/>'s dlls.
    /// </summary>
    /// <remarks>
    /// Case, surrounding space and a trailing full stop are ignored; nothing else is. "NVIDIA Corp"
    /// is not NVIDIA Corporation, and a rule loose enough to accept it would accept the next thing
    /// too. An unknown vendor accepts nobody, so a type added without a vendor fails closed.
    /// </remarks>
    public static bool IsAcceptedPublisher(DllVendor vendor, string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
        {
            return false;
        }

        if (_accepted.TryGetValue(vendor, out var names) == false)
        {
            return false;
        }

        var seen = Normalise(publisher);
        foreach (var name in names)
        {
            if (string.Equals(Normalise(name), seen, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The name a message should say was expected, for a vendor.</summary>
    public static string ExpectedPublisher(DllVendor vendor)
    {
        return _accepted.TryGetValue(vendor, out var names) && names.Length > 0
            ? names[0]
            : "an unknown vendor";
    }

    static string Normalise(string name)
    {
        var trimmed = name.Trim();
        while (trimmed.EndsWith('.'))
        {
            trimmed = trimmed.Substring(0, trimmed.Length - 1).TrimEnd();
        }

        return trimmed;
    }
}
