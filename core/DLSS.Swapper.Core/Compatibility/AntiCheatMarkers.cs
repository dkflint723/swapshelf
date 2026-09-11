using System;
using System.Collections.Generic;

namespace DLSS_Swapper.Compatibility;

/// <summary>
/// Recognises the anti-cheat systems that leave a footprint in a game's install folder.
/// </summary>
/// <remarks>
/// <para>
/// Advisory, never a gate. A swapped dll is not cheating, but some anti-cheat systems do object
/// to a game folder that differs from what shipped, and the warning about that used to appear once
/// per installation for every game at once. Knowing which games actually carry one lets the
/// warning name them, and lets a game that gains one after being acknowledged be asked about
/// again.
/// </para>
/// <para>
/// Works on paths the scan has already enumerated, so it costs no extra walk. The list is
/// deliberately short and specific: a marker that is also an ordinary word - "Vanguard" is a
/// Call of Duty title as well as Riot's anti-cheat - is left out, because a false positive here
/// is a warning the user learns to click through. The generic "anticheat" fragment comes last so
/// a named system wins when both would match.
/// </para>
/// </remarks>
public static class AntiCheatMarkers
{
    static readonly (string Name, string[] Fragments)[] _known =
    {
        ("Easy Anti-Cheat", new[] { "easyanticheat" }),
        ("BattlEye", new[] { "battleye", "beclient_x64", "beclient_x86", "beservice" }),
        ("Ricochet", new[] { "ricochet" }),
        ("GameGuard", new[] { "gameguard", "npgg" }),
        ("nProtect", new[] { "nprotect" }),
        ("PunkBuster", new[] { "punkbuster", "pbcl.dll", "pbsv.dll", "pbag.dll" }),
        ("XIGNCODE", new[] { "xigncode", "xhunter" }),
        ("Denuvo Anti-Cheat", new[] { "denuvo-anti-cheat", "denuvoanticheat" }),
        ("Anti-cheat", new[] { "anticheat", "anti-cheat", "anti_cheat" }),
    };

    /// <summary>
    /// The name of the first anti-cheat system whose footprint appears in <paramref name="relativePaths"/>,
    /// or null when none does.
    /// </summary>
    /// <param name="relativePaths">
    /// Paths relative to the game's install folder - dll paths from the scan, top-level folder
    /// names, anything already in hand. Case and separator do not matter.
    /// </param>
    public static string? Detect(IEnumerable<string> relativePaths)
    {
        var normalised = new List<string>();
        foreach (var path in relativePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            normalised.Add(path.Replace('\\', '/').ToLowerInvariant());
        }

        if (normalised.Count == 0)
        {
            return null;
        }

        foreach (var (name, fragments) in _known)
        {
            foreach (var fragment in fragments)
            {
                foreach (var path in normalised)
                {
                    if (path.Contains(fragment, StringComparison.Ordinal))
                    {
                        return name;
                    }
                }
            }
        }

        return null;
    }
}
