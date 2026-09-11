using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DLSS_Swapper.Compatibility;
using DLSS_Swapper.Data;
using DLSS_Swapper.Data.ManuallyAdded;
using DLSS_Swapper.Helpers;
using DLSS_Swapper.Swapping;
using Xunit;

namespace DLSS_Swapper.App.Tests;

/// <summary>
/// Gathering the evidence for a confidence label from a real game and a real record.
/// </summary>
/// <remarks>
/// The rules themselves are proven in the core tests. These pin where the evidence comes from:
/// that "shipped" means the saved original, or the file itself when the app never swapped it; that
/// history is read for the one rule that needs it; and that the Streamline fact recorded at scan
/// reaches the rule that lowers frame generation swaps.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public class SwapConfidenceTests
{
    static ManuallyAddedGame GameWith(TemporaryDatabase database, string id, GameAssetType assetType, string currentVersion, string currentHash, string? swappedHash = null, (string Version, string Hash)? backup = null)
    {
        var game = new ManuallyAddedGame(id) { Title = id, InstallPath = database.GameFolder };
        var target = Path.Combine(database.GameFolder, id + ".dll");

        game.GameAssets.Add(new GameAsset()
        {
            Id = game.ID,
            AssetType = assetType,
            Path = target,
            Version = currentVersion,
            Hash = currentHash,
            SwappedHash = swappedHash,
        });

        if (backup is not null)
        {
            game.GameAssets.Add(new GameAsset()
            {
                Id = game.ID,
                AssetType = DLLManager.Instance.GetAssetBackupType(assetType),
                Path = DllSwapExecutor.GetBackupPath(target),
                Version = backup.Value.Version,
                Hash = backup.Value.Hash,
            });
        }

        return game;
    }

    static DLLRecord Record(GameAssetType assetType, string version, string hash, bool dev = false, bool signed = true)
    {
        return new DLLRecord()
        {
            AssetType = assetType,
            Version = version,
            MD5Hash = hash,
            IsDevFile = dev,
            IsSignatureValid = signed,
        };
    }

    [Fact]
    public async Task TheSavedOriginalIsWhatTheGameShipped()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        // Swapped from 3.7 to 310.2; the .dlsss beside the game is the 3.7 the game shipped.
        var game = GameWith(database, "conf-backup", GameAssetType.DLSS, "310.2.1.0", "NEW", swappedHash: "NEW", backup: ("3.7.20.0", "ORIG"));

        var original = SwapConfidence.Assess(game, GameAssetType.DLSS, Record(GameAssetType.DLSS, "3.7.20.0", "ORIG"), history: null);
        Assert.Equal(ConfidenceLevel.KnownGood, original.Level);
        Assert.Equal(ConfidenceReason.ShippedFile, original.Reason);

        // A version between the two lines is judged against what shipped, not against what is installed.
        var between = SwapConfidence.Assess(game, GameAssetType.DLSS, Record(GameAssetType.DLSS, "3.8.10.0", "MID"), history: null);
        Assert.Equal(ConfidenceReason.SameLine, between.Reason);
    }

    [Fact]
    public async Task AFileTheAppNeverSwappedIsWhatTheGameShipped()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var game = GameWith(database, "conf-never-swapped", GameAssetType.DLSS, "3.7.20.0", "ORIG");

        // The installed file, which is also the shipped one: shipped wins.
        var installed = SwapConfidence.Assess(game, GameAssetType.DLSS, Record(GameAssetType.DLSS, "3.7.20.0", "ORIG"), history: null);
        Assert.Equal(ConfidenceReason.ShippedFile, installed.Reason);

        var newer = SwapConfidence.Assess(game, GameAssetType.DLSS, Record(GameAssetType.DLSS, "310.2.1.0", "NEW"), history: null);
        Assert.Equal(ConfidenceLevel.Likely, newer.Level);
        Assert.Equal(ConfidenceReason.NewerLine, newer.Reason);
    }

    [Fact]
    public async Task AFileReplacedOutsideTheAppAfterASwapHasNoVersionEvidence()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        // Swapped once (swapped hash recorded), then the .dlsss was lost: nothing says what shipped.
        var game = GameWith(database, "conf-no-shipped", GameAssetType.DLSS, "310.2.1.0", "NEW", swappedHash: "OLDSWAP");

        var result = SwapConfidence.Assess(game, GameAssetType.DLSS, Record(GameAssetType.DLSS, "310.3.0.0", "NEWER"), history: null);

        Assert.Equal(ConfidenceLevel.Unknown, result.Level);
        Assert.Equal(ConfidenceReason.NoEvidence, result.Reason);
    }

    [Fact]
    public async Task HistoryMakesAVersionSwappedHereBeforeLikely()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var game = GameWith(database, "conf-history", GameAssetType.DLSS_G, "310.2.1.0", "NEW", swappedHash: "NEW");
        game.UsesStreamline = true;
        var candidate = Record(GameAssetType.DLSS_G, "3.7.10.0", "OLDFG");

        var without = SwapConfidence.Assess(game, GameAssetType.DLSS_G, candidate, history: null);
        Assert.NotEqual(ConfidenceReason.SwappedHereBefore, without.Reason);

        var history = new List<GameHistory>()
        {
            new GameHistory() { GameId = game.ID, EventType = GameHistoryEventType.DLLSwapped, AssetType = GameAssetType.DLSS_G, AssetVersion = candidate.DisplayName },
        };
        var with = SwapConfidence.Assess(game, GameAssetType.DLSS_G, candidate, history);

        Assert.Equal(ConfidenceLevel.Likely, with.Level);
        Assert.Equal(ConfidenceReason.SwappedHereBefore, with.Reason);
    }

    [Fact]
    public async Task StreamlineLowersFrameGenerationAcrossLinesAndOnlyThat()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var game = GameWith(database, "conf-streamline", GameAssetType.DLSS_G, "3.7.10.0", "ORIG");
        var candidate = Record(GameAssetType.DLSS_G, "310.2.1.0", "NEW");

        game.UsesStreamline = false;
        Assert.Equal(ConfidenceReason.NewerLine, SwapConfidence.Assess(game, GameAssetType.DLSS_G, candidate, null).Reason);

        game.UsesStreamline = true;
        var lowered = SwapConfidence.Assess(game, GameAssetType.DLSS_G, candidate, null);
        Assert.Equal(ConfidenceLevel.Experimental, lowered.Level);
        Assert.Equal(ConfidenceReason.StreamlineFrameGeneration, lowered.Reason);
    }

    [Fact]
    public async Task DevAndUnsignedBuildsAreExperimental()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var game = GameWith(database, "conf-dev", GameAssetType.DLSS, "3.7.20.0", "ORIG");

        Assert.Equal(ConfidenceReason.DevBuild, SwapConfidence.Assess(game, GameAssetType.DLSS, Record(GameAssetType.DLSS, "310.2.1.0", "DEV", dev: true), null).Reason);
        Assert.Equal(ConfidenceReason.UntrustedSignature, SwapConfidence.Assess(game, GameAssetType.DLSS, Record(GameAssetType.DLSS, "310.2.1.0", "UNS", signed: false), null).Reason);
    }

    [Fact]
    public void TheDisplayHasADifferentShapeForEveryLevel()
    {
        var glyphs = new HashSet<string>();
        foreach (var level in Enum.GetValues<ConfidenceLevel>())
        {
            var display = ConfidenceDisplay.For(new Confidence(level, ConfidenceReason.NoEvidence));
            Assert.True(glyphs.Add(display.Glyph), $"{level} shares a glyph with another level");
            Assert.False(string.IsNullOrWhiteSpace(display.Label));
            Assert.Contains(display.Label, display.Accessible);
        }
    }

    [Fact]
    public async Task TheStreamlineFactIsRecordedFromTheScanAndSurvivesTheDatabase()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var game = new ManuallyAddedGame("conf-sl-persist") { Title = "Streamline Game", InstallPath = database.GameFolder };

        game.RecordStreamline(new[] { Path.Combine(database.GameFolder, "nvngx_dlss.dll"), Path.Combine(database.GameFolder, "SL.Interposer.DLL") });
        Assert.True(game.UsesStreamline);
        await game.SaveToDatabaseAsync();

        var reloaded = await Database.Instance.Connection.Table<ManuallyAddedGame>().FirstOrDefaultAsync(x => x.ID == game.ID);
        Assert.NotNull(reloaded);
        Assert.True(reloaded.UsesStreamline);

        // And a scan that no longer sees it clears it.
        game.RecordStreamline(new[] { Path.Combine(database.GameFolder, "nvngx_dlss.dll") });
        Assert.False(game.UsesStreamline);
    }

    [Fact]
    public async Task TheGamePageRowSaysSoForNvidiaDllsOnly()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        using var manifest = new ManifestScope();

        var game = GameWith(database, "conf-row", GameAssetType.DLSS_G, "3.7.10.0", "ORIG");
        game.GameAssets.Add(new GameAsset() { Id = game.ID, AssetType = GameAssetType.XeSS, Path = Path.Combine(database.GameFolder, "libxess.dll"), Version = "2.0.1.0", Hash = "XESS" });
        game.UsesStreamline = true;

        var clause = ResourceHelper.GetString("GamePage_Row_Streamline");
        Assert.Contains(clause, UpscalerRowStatus.For(game, GameAssetType.DLSS_G).Sentence);
        Assert.DoesNotContain(clause, UpscalerRowStatus.For(game, GameAssetType.XeSS).Sentence);

        game.UsesStreamline = false;
        Assert.DoesNotContain(clause, UpscalerRowStatus.For(game, GameAssetType.DLSS_G).Sentence);
    }
}
