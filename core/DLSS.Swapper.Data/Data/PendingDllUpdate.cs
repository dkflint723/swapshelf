using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DLSS_Swapper.Dlls;
using DLSS_Swapper.Helpers;

namespace DLSS_Swapper.Data;

/// <summary>
/// One file an update run would replace: which game, which engine, and the two versions.
/// </summary>
/// <remarks>
/// The preview sheet exists so the app stops writing into game folders without first saying what it
/// is about to touch. That means this list has to be the same list the run works from, not a
/// description of it written separately, so the run takes these rows rather than recomputing.
/// </remarks>
public partial class PendingDllUpdate : ObservableObject
{
    /// <summary>
    /// Private so only <see cref="ForGames"/> can build one, and so the XAML type generator does
    /// not emit an activator it cannot compile against required members.
    /// </summary>
    PendingDllUpdate()
    {
    }

    public required Game Game { get; init; }

    public required GameAssetType AssetType { get; init; }

    public required string GameTitle { get; init; }

    /// <summary>The engine's display name, such as "DLSS Ray Reconstruction".</summary>
    public required string EngineName { get; init; }

    /// <summary>What is installed now.</summary>
    public required string FromVersion { get; init; }

    /// <summary>What it would be replaced with.</summary>
    public required string ToVersion { get; init; }

    /// <summary>How much is known about this version in this game, for the row and its tooltip.</summary>
    public required ConfidenceDisplay Confidence { get; init; }

    /// <summary>Checked by default: the sheet offers to leave files out, it does not ask for opt in.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;

    /// <summary>
    /// The whole row in one line, for the tick box that decides whether it is written.
    /// </summary>
    /// <remarks>
    /// The row is four columns of an ItemsControl, so there is no ListViewItem to gather them: a
    /// screen reader met a column of "checkbox, checked" with no game named, immediately before a
    /// batch that writes into game folders. Everything the row shows is said here instead.
    /// </remarks>
    public string AccessibleDescription =>
        ResourceHelper.GetFormattedResourceTemplate("Preview_RowDescriptionTemplate", GameTitle, EngineName, FromVersion, ToVersion)
        + ". " + Confidence.Accessible;

    /// <summary>
    /// Every out of date file across the given games, newest available version for each.
    /// </summary>
    /// <remarks>
    /// Games with updates turned off produce no rows. They are refused by the run as well, but a
    /// list that offered a file the run would then silently skip would be a worse lie than the one
    /// this sheet exists to fix.
    /// </remarks>
    internal static List<PendingDllUpdate> ForGames(IEnumerable<Game> games)
    {
        var pendingUpdates = new List<PendingDllUpdate>();

        foreach (var game in games)
        {
            if (game.SkipUpdates)
            {
                continue;
            }

            foreach (var assetType in game.OutdatedAssetTypes)
            {
                var latestRecord = DLLManager.Instance.GetLatestRecord(assetType);
                if (latestRecord is null)
                {
                    // Nothing to swap to, so nothing to promise.
                    continue;
                }

                // Last one wins, matching how a game's current asset for a type is resolved
                // everywhere else when a game ships the same dll in more than one folder.
                var currentAsset = game.GameAssets.LastOrDefault(x => x.AssetType == assetType);

                pendingUpdates.Add(new PendingDllUpdate()
                {
                    Game = game,
                    AssetType = assetType,
                    GameTitle = game.Title,
                    EngineName = DLLManager.Instance.GetAssetTypeName(assetType),
                    FromVersion = currentAsset?.DisplayVersion ?? string.Empty,
                    ToVersion = latestRecord.DisplayVersion,
                    // Without history, which the sheet is built too synchronously to wait for. The
                    // one rule that reads history simply does not fire here.
                    Confidence = ConfidenceDisplay.For(SwapConfidence.Assess(game, assetType, latestRecord, history: null)),
                });
            }
        }

        return pendingUpdates;
    }
}
