using System;
using System.Threading.Tasks;
using DLSS_Swapper.Data;
using DLSS_Swapper.Data.ManuallyAdded;
using DLSS_Swapper.Helpers;
using Xunit;

namespace DLSS_Swapper.App.Tests;

/// <summary>
/// A game that is running is a reason not to start a swap or a restore, not an error to recover from.
/// </summary>
/// <remarks>
/// <para>
/// The executor already copes with a running game - the rename fails with a sharing violation and
/// everything rolls back untouched. That is proven in its own tests and it stays. What these pin is
/// the step in front of it: asked before anything is staged, the answer is "close the game", and
/// the game is not touched at all.
/// </para>
/// <para>
/// The process check itself is swapped out. A test cannot run a game, and would not want to.
/// </para>
/// </remarks>
[Collection(DatabaseCollection.Name)]
public class RunningGameGateTests : IDisposable
{
    readonly Func<string, bool> _originalCheck = Game.IsRunningCheck;

    public void Dispose()
    {
        Game.IsRunningCheck = _originalCheck;
    }

    static ManuallyAddedGame GameWithASwappedDll(string id, string installPath)
    {
        var game = new ManuallyAddedGame(id)
        {
            Title = id,
            InstallPath = installPath,
        };

        var current = System.IO.Path.Combine(installPath, "nvngx_dlss.dll");
        game.GameAssets.Add(new GameAsset()
        {
            Id = game.ID,
            AssetType = GameAssetType.DLSS,
            Path = current,
            Version = "310.8.0.0",
            Hash = "swapped",
        });
        game.GameAssets.Add(new GameAsset()
        {
            Id = game.ID,
            AssetType = GameAssetType.DLSS_BACKUP,
            Path = current + ".dlsss",
            Version = "310.1.0.0",
            Hash = "original",
        });
        return game;
    }

    [Fact]
    public async Task ARunningGameIsNotRestored()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        string? asked = null;
        Game.IsRunningCheck = path => { asked = path; return true; };

        var game = GameWithASwappedDll("gate-running", database.GameFolder);
        var result = await game.ResetDllAsync(GameAssetType.DLSS);

        Assert.False(result.Success);
        Assert.Equal(ResourceHelper.GetString("Game_GameRunning_CloseFirst"), result.Message);
        Assert.False(result.PromptToRelaunchAsAdmin);

        // It asked about this game's folder, not some other path.
        Assert.Equal(database.GameFolder, asked);

        // Nothing was rewritten: both records are exactly as they were.
        Assert.Equal(2, game.GameAssets.Count);
        Assert.Contains(game.GameAssets, x => x.AssetType == GameAssetType.DLSS_BACKUP && x.Hash == "original");
    }

    [Fact]
    public async Task AGameThatIsNotRunningGetsPastTheGate()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        Game.IsRunningCheck = _ => false;

        var game = GameWithASwappedDll("gate-idle", database.GameFolder);
        var result = await game.ResetDllAsync(GameAssetType.DLSS);

        // No files exist on disk, so the executor reports the backup missing. The point is that the
        // gate is the ONLY difference between this and the test above: the answer is now about the
        // files, not about the game running.
        Assert.False(result.Success);
        Assert.NotEqual(ResourceHelper.GetString("Game_GameRunning_CloseFirst"), result.Message);
    }

    [Fact]
    public async Task TheGateIsAskedBeforeTheExecutorEverRuns()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var calls = 0;
        Game.IsRunningCheck = _ => { calls++; return true; };

        var game = GameWithASwappedDll("gate-order", database.GameFolder);
        await game.ResetDllAsync(GameAssetType.DLSS);

        Assert.Equal(1, calls);
    }
}
