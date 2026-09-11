using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DLSS_Swapper.Compatibility;
using DLSS_Swapper.Dlls;
using DLSS_Swapper.Swapping;
using DLSS_Swapper.Versioning;

namespace DLSS_Swapper.Data;

/// <summary>
/// Gathers what is known about a game and a candidate dll, and asks the rules.
/// </summary>
/// <remarks>
/// <para>
/// The version the game shipped with is the saved original when there is one. When the app has
/// never swapped this dll - no swapped hash on the row - the file sitting there IS what the game
/// shipped, so that is used. A game whose dll was replaced outside the app after a swap has
/// neither, and gets no version evidence rather than a guess.
/// </para>
/// <para>
/// History is optional because the batch preview builds its rows synchronously; without it the
/// one rule that reads history simply never fires. The picker loads it and re-asks.
/// </para>
/// </remarks>
internal static class SwapConfidence
{
    internal static Confidence Assess(Game game, GameAssetType assetType, DLLRecord candidate, IReadOnlyList<GameHistory>? history)
    {
        var definition = DllTypes.ForAssetType(assetType);

        // Last one wins, matching how a game's current asset for a type is resolved everywhere else
        // when a game ships the same dll in more than one folder.
        var current = game.GameAssets.LastOrDefault(x => x.AssetType == assetType);

        GameAsset? backup = null;
        if (definition is not null && current is not null)
        {
            var backupPath = DllSwapExecutor.GetBackupPath(current.Path);
            backup = game.GameAssets.FirstOrDefault(x => x.AssetType == definition.BackupAssetType
                    && string.Equals(x.Path, backupPath, StringComparison.OrdinalIgnoreCase))
                ?? game.GameAssets.FirstOrDefault(x => x.AssetType == definition.BackupAssetType);
        }

        var neverSwappedHere = current is not null && string.IsNullOrWhiteSpace(current.SwappedHash);
        var shipped = backup ?? (neverSwappedHere ? current : null);

        var shippedHash = backup?.Hash;
        if (string.IsNullOrWhiteSpace(shippedHash) && current is not null)
        {
            // The mirrored original, when the user keeps those, remembers the hash even after the
            // .dlsss beside the game is gone.
            shippedHash = OriginalsStore.RecordedHash(game.ID, current.Path);
        }
        if (string.IsNullOrWhiteSpace(shippedHash) && neverSwappedHere)
        {
            shippedHash = current!.Hash;
        }

        var evidence = new ConfidenceEvidence()
        {
            Family = ConfidenceRules.FamilyOf(assetType),
            ShippedRank = shipped is null ? null : Rank(assetType, definition, shipped),
            CandidateRank = DllVersionRanking.TryGetRank(assetType, candidate.InternalName, candidate.Version, out var candidateRank) ? candidateRank : null,
            CandidateIsShippedFile = HashesMatch(shippedHash, candidate.MD5Hash),
            CandidateIsInstalled = current is not null && HashesMatch(current.Hash, candidate.MD5Hash),
            CandidateSwappedHereBefore = history?.Any(x => x.EventType == GameHistoryEventType.DLLSwapped
                && x.AssetType == assetType
                && string.Equals(x.AssetVersion, candidate.DisplayName, StringComparison.Ordinal)) ?? false,
            CandidateIsDevBuild = candidate.IsDevFile,
            CandidateSignatureTrusted = candidate.IsSignatureValid,
            GameUsesStreamline = game.UsesStreamline,
        };

        return ConfidenceRules.Assess(evidence);
    }

    /// <summary>This game's history, for the one rule that reads it.</summary>
    internal static async Task<IReadOnlyList<GameHistory>> LoadHistoryAsync(Game game)
    {
        using (await Database.Instance.Mutex.LockAsync())
        {
            return await Database.Instance.Connection.Table<GameHistory>()
                .Where(x => x.GameId == game.ID)
                .ToListAsync()
                .ConfigureAwait(false);
        }
    }

    static ulong? Rank(GameAssetType assetType, DllTypeDefinition? definition, GameAsset asset)
    {
        // Only the types ranked by SDK version pay for DisplayVersion, which for FSR reads records
        // and, failing that, the file.
        var internalVersion = definition?.VersionFromInternalName == true ? asset.DisplayVersion : null;
        return DllVersionRanking.TryGetRank(assetType, internalVersion, asset.Version, out var rank) ? rank : null;
    }

    static bool HashesMatch(string? a, string? b)
    {
        return string.IsNullOrWhiteSpace(a) == false
            && string.IsNullOrWhiteSpace(b) == false
            && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
