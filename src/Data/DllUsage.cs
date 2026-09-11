using System.Collections.Generic;
using DLSS_Swapper.Dlls;
using DLSS_Swapper.Helpers;
using Microsoft.UI.Xaml;

namespace DLSS_Swapper.Data;

/// <summary>
/// How many games have a given dll in place right now.
/// </summary>
/// <remarks>
/// The one question the dll list could never answer. Everything else on that page describes the
/// file; this describes what would break if it went, which is the only thing anyone needs before
/// deleting one.
/// </remarks>
public static class DllUsage
{
    /// <summary>
    /// Whether a game currently has this exact dll installed.
    /// </summary>
    /// <remarks>
    /// Hash first and version only as a fallback, matching how an installed dll is resolved to a
    /// known version everywhere else: a hash is exact, but a dll a game shipped with is often not
    /// in the manifest at all and only its file version can be compared.
    /// </remarks>
    public static bool IsUsedBy(GameAssetType assetType, string md5Hash, string version, Game game)
        => InstalledDllMatch.IsUsedBy(assetType, md5Hash, version, game);

    public static int CountGamesUsing(GameAssetType assetType, string md5Hash, string version, IEnumerable<Game> games)
    {
        var count = 0;

        foreach (var game in games)
        {
            if (IsUsedBy(assetType, md5Hash, version, game))
            {
                ++count;
            }
        }

        return count;
    }

    /// <summary>
    /// The same answer, shaped for the row: what is shown when the file is in use, and what is
    /// shown when it is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two functions rather than one plus a converter, because an <c>x:Bind</c> to a function
    /// ignores <c>Converter</c> entirely and fails the build rather than at runtime. Both sit here
    /// beside the rule they ask, so neither can drift from the count the row is showing.
    /// </para>
    /// <para>
    /// These take the count rather than working it out, and that is the whole point. They used to
    /// take the asset type, hash and version and go and count. An <c>x:Bind</c> to a function
    /// re-evaluates when its arguments change, and none of those three change when a dll is swapped
    /// into a game - so the row kept the number it was first drawn with. Taking
    /// <see cref="DLLRecord.GamesUsingCount"/> puts an argument that does change in front of them.
    /// Every one of these bindings needs <c>Mode=OneWay</c> to benefit; x:Bind is OneTime by
    /// default, which is how the original went unnoticed.
    /// </para>
    /// </remarks>
    public static Visibility UsedVisibility(int gamesUsingCount)
    {
        return gamesUsingCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public static Visibility NotUsedVisibility(int gamesUsingCount)
    {
        return gamesUsingCount > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// The words for a count.
    /// </summary>
    /// <remarks>
    /// Separate from the count itself because the library lives on a singleton that cannot be built
    /// outside the app, and the wording is the part worth pinning down.
    /// </remarks>
    public static string DescribeCount(int count)
    {
        if (count == 0)
        {
            return ResourceHelper.GetString("Upscalers_NotUsed");
        }

        return count == 1
            ? ResourceHelper.GetString("Upscalers_UsedByOneGame")
            : ResourceHelper.GetFormattedResourceTemplate("Upscalers_UsedByGamesTemplate", count);
    }
}
