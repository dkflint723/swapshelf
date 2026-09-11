using System.Collections.Generic;
using DLSS_Swapper.Data;
using Microsoft.UI.Xaml;
using Xunit;

namespace DLSS_Swapper.App.Tests;

/// <summary>
/// The usage count as the row actually reads it: a property that changes, rather than an answer
/// worked out once while the row was being drawn.
/// </summary>
/// <remarks>
/// <para>
/// The upscalers page used to bind straight to a function over the record - the asset type, the
/// hash and the version went in, a count came out. An <c>x:Bind</c> to a function re-evaluates when
/// its ARGUMENTS change, and swapping a dll into a game changes none of those three. What changed
/// was the games list, which was never an argument. So the count was whatever had been true the
/// moment the row was first drawn, kept for the life of a page that is also
/// <c>NavigationCacheMode="Required"</c>.
/// </para>
/// <para>
/// It read as a bug in imported dlls because those are the ones somebody imports and then swaps
/// straight away: the row is drawn while the file is in no games, says "Not used" perfectly
/// correctly, and never says anything else. Downloaded dlls had it too and hid it, because most of
/// two hundred rows really are unused.
/// </para>
/// <para>
/// So what is worth pinning here is not the arithmetic - <see cref="DllUsageTests"/> has that - but
/// that the number is allowed to change and says so when it does.
/// </para>
/// </remarks>
public class DllUsageCountTests
{
    static GameAsset Asset(string gameId, GameAssetType assetType, string version, string hash = "")
    {
        return new GameAsset()
        {
            Id = gameId,
            AssetType = assetType,
            Path = $@"C:\game\{assetType}.dll",
            Version = version,
            Size = 1024,
            Hash = hash,
        };
    }

    static TestGame GameWith(string id, params GameAsset[] assets)
    {
        var game = new TestGame(id);
        game.GameAssets.AddRange(assets);
        return game;
    }

    [Fact]
    public void TheCountSaysSoWhenItChanges()
    {
        // The whole fix. Without a notification the row keeps its first answer for ever, which is
        // exactly what it used to do.
        var record = new DLLRecord() { AssetType = GameAssetType.DLSS, MD5Hash = "abc123", Version = "310.1.0.0" };

        var changed = new List<string>();
        record.PropertyChanged += (sender, args) => changed.Add(args.PropertyName ?? string.Empty);

        record.GamesUsingCount = 3;

        Assert.Equal(3, record.GamesUsingCount);
        Assert.Contains(nameof(DLLRecord.GamesUsingCount), changed);
    }

    [Fact]
    public void SettingTheSameCountSaysNothing()
    {
        // Every game change recounts every dll. Almost none of them move, and a notification per
        // record per change would redraw two hundred rows to say nothing happened.
        var record = new DLLRecord() { AssetType = GameAssetType.DLSS, MD5Hash = "abc123", Version = "310.1.0.0" };
        record.GamesUsingCount = 2;

        var changed = new List<string>();
        record.PropertyChanged += (sender, args) => changed.Add(args.PropertyName ?? string.Empty);

        record.GamesUsingCount = 2;

        Assert.DoesNotContain(nameof(DLLRecord.GamesUsingCount), changed);
    }

    [Fact]
    public void ADllStartsOutCountedAsUnused()
    {
        // A record that has never been counted must not claim to be in use, because the row draws
        // before anything has had a chance to count.
        var record = new DLLRecord() { AssetType = GameAssetType.DLSS, MD5Hash = "abc123", Version = "310.1.0.0" };

        Assert.Equal(0, record.GamesUsingCount);
        Assert.Equal(Visibility.Collapsed, DllUsage.UsedVisibility(record.GamesUsingCount));
        Assert.Equal(Visibility.Visible, DllUsage.NotUsedVisibility(record.GamesUsingCount));
    }

    [Fact]
    public void SwappingItIntoGamesMovesTheCountOffZero()
    {
        // The reported bug, in the order it happens: import a dll, nothing is using it, then swap it
        // into games. The count has to follow, and the two visibilities have to swap over with it.
        var record = new DLLRecord() { AssetType = GameAssetType.DLSS, MD5Hash = "imported", Version = "310.8.0.0" };

        var games = new List<Game>() { GameWith("count_1"), GameWith("count_2") };

        record.GamesUsingCount = DllUsage.CountGamesUsing(record.AssetType, record.MD5Hash, record.Version, games);
        Assert.Equal(0, record.GamesUsingCount);
        Assert.Equal(Visibility.Visible, DllUsage.NotUsedVisibility(record.GamesUsingCount));

        // Now it is in place in both of them, the way a swap leaves things.
        games[0].GameAssets.Add(Asset("count_1", GameAssetType.DLSS, "310.8.0.0", "imported"));
        games[1].GameAssets.Add(Asset("count_2", GameAssetType.DLSS, "310.8.0.0", "imported"));

        record.GamesUsingCount = DllUsage.CountGamesUsing(record.AssetType, record.MD5Hash, record.Version, games);

        Assert.Equal(2, record.GamesUsingCount);
        Assert.Equal(Visibility.Visible, DllUsage.UsedVisibility(record.GamesUsingCount));
        Assert.Equal(Visibility.Collapsed, DllUsage.NotUsedVisibility(record.GamesUsingCount));
    }

    [Fact]
    public void RestoringTheOriginalTakesTheCountBackDown()
    {
        // The other direction, which a count that only ever grows would get wrong.
        var record = new DLLRecord() { AssetType = GameAssetType.DLSS, MD5Hash = "imported", Version = "310.8.0.0" };
        var game = GameWith("count_3", Asset("count_3", GameAssetType.DLSS, "310.8.0.0", "imported"));
        var games = new List<Game>() { game };

        record.GamesUsingCount = DllUsage.CountGamesUsing(record.AssetType, record.MD5Hash, record.Version, games);
        Assert.Equal(1, record.GamesUsingCount);

        game.GameAssets.Clear();
        game.GameAssets.Add(Asset("count_3", GameAssetType.DLSS, "310.1.0.0", "shipped"));

        record.GamesUsingCount = DllUsage.CountGamesUsing(record.AssetType, record.MD5Hash, record.Version, games);

        Assert.Equal(0, record.GamesUsingCount);
        Assert.Equal(Visibility.Collapsed, DllUsage.UsedVisibility(record.GamesUsingCount));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(14, true)]
    public void TheTwoVisibilitiesAreAlwaysOpposites(int count, bool expectedUsed)
    {
        // They sit on two controls in the same cell. If they ever agree, the row shows the count
        // twice or not at all.
        var used = DllUsage.UsedVisibility(count);
        var notUsed = DllUsage.NotUsedVisibility(count);

        Assert.Equal(expectedUsed ? Visibility.Visible : Visibility.Collapsed, used);
        Assert.NotEqual(used, notUsed);
    }
}
