using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using DLSS_Swapper.Data;
using DLSS_Swapper.Data.ManuallyAdded;
using DLSS_Swapper.Helpers;
using DLSS_Swapper.Swapping;
using Xunit;

namespace DLSS_Swapper.App.Tests;

/// <summary>
/// Restore asks before writing over a dll that changed since the app last swapped it.
/// </summary>
/// <remarks>
/// <para>
/// The executor's refusal is proven in its own tests. What these pin is the game's side of it:
/// which hash it expects the file to have, that a refusal comes back as a question rather than an
/// error, that answering the question restores, and that the hash a swap records survives the
/// database - a swap remembered only in memory would be forgotten by the next launch, and every
/// restore after that would ask about nothing.
/// </para>
/// <para>
/// Real files in a throwaway folder, because the hashes have to be of something.
/// </para>
/// </remarks>
[Collection(DatabaseCollection.Name)]
public class RestoreRespectsLaterChangesTests : IDisposable
{
    readonly Func<string, bool> _originalCheck = Game.IsRunningCheck;

    public RestoreRespectsLaterChangesTests()
    {
        Game.IsRunningCheck = _ => false;
    }

    public void Dispose()
    {
        Game.IsRunningCheck = _originalCheck;
    }

    static readonly byte[] Original = Fill("original");
    static readonly byte[] Swapped = Fill("swapped");
    static readonly byte[] Edited = Fill("edited by hand");

    static byte[] Fill(string seed)
    {
        var bytes = new byte[2048];
        var pattern = Encoding.UTF8.GetBytes(seed);
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = pattern[i % pattern.Length];
        }
        return bytes;
    }

    static string Md5(byte[] bytes) => Convert.ToHexString(MD5.HashData(bytes));

    /// <summary>
    /// A game whose dll the app swapped: the file on disk, the saved original beside it, and rows
    /// for both. <paramref name="swappedHash"/> is what the swap recorded; <paramref name="rowHash"/>
    /// what the last scan saw, which defaults to the swapped file.
    /// </summary>
    static ManuallyAddedGame SwappedGame(TemporaryDatabase database, string id, byte[] onDisk, string? swappedHash, string? rowHash = null)
    {
        var target = Path.Combine(database.GameFolder, "nvngx_dlss.dll");
        File.WriteAllBytes(target, onDisk);
        File.WriteAllBytes(DllSwapExecutor.GetBackupPath(target), Original);

        var game = new ManuallyAddedGame(id) { Title = id, InstallPath = database.GameFolder };
        game.GameAssets.Add(new GameAsset()
        {
            Id = game.ID,
            AssetType = GameAssetType.DLSS,
            Path = target,
            Version = "310.8.0.0",
            Hash = rowHash ?? Md5(Swapped),
            SwappedHash = swappedHash,
        });
        game.GameAssets.Add(new GameAsset()
        {
            Id = game.ID,
            AssetType = GameAssetType.DLSS_BACKUP,
            Path = DllSwapExecutor.GetBackupPath(target),
            Version = "310.1.0.0",
            Hash = Md5(Original),
        });
        return game;
    }

    static string TargetIn(TemporaryDatabase database) => Path.Combine(database.GameFolder, "nvngx_dlss.dll");

    [Fact]
    public async Task ADllStillAsTheAppLeftItIsRestoredWithoutAQuestion()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var game = SwappedGame(database, "restore-unchanged", onDisk: Swapped, swappedHash: Md5(Swapped));

        var result = await game.ResetDllAsync(GameAssetType.DLSS);

        Assert.True(result.Success);
        Assert.False(result.NeedsConfirmation);
        Assert.Equal(Original, File.ReadAllBytes(TargetIn(database)));
        Assert.False(File.Exists(DllSwapExecutor.GetBackupPath(TargetIn(database))));
    }

    [Fact]
    public async Task ADllChangedSinceTheSwapIsLeftAloneAndTheQuestionIsAsked()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var game = SwappedGame(database, "restore-changed", onDisk: Edited, swappedHash: Md5(Swapped));

        var result = await game.ResetDllAsync(GameAssetType.DLSS);

        Assert.False(result.Success);
        Assert.True(result.NeedsConfirmation);
        Assert.Equal(SwapFailure.TargetChanged, result.Failure);
        Assert.Equal(ResourceHelper.GetString("Game_Reset_TargetChanged"), result.Message);
        Assert.False(result.PromptToRelaunchAsAdmin);

        // The edit is intact, the original is still saved, and the rows say so.
        Assert.Equal(Edited, File.ReadAllBytes(TargetIn(database)));
        Assert.True(File.Exists(DllSwapExecutor.GetBackupPath(TargetIn(database))));
        Assert.Equal(2, game.GameAssets.Count);
        Assert.Contains(game.GameAssets, x => x.AssetType == GameAssetType.DLSS_BACKUP);
    }

    [Fact]
    public async Task AnsweringRestoreAnywayRestores()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var game = SwappedGame(database, "restore-anyway", onDisk: Edited, swappedHash: Md5(Swapped));

        var asked = await game.ResetDllAsync(GameAssetType.DLSS);
        Assert.True(asked.NeedsConfirmation);

        var answered = await game.ResetDllAsync(GameAssetType.DLSS, restoreChangedFiles: true);

        Assert.True(answered.Success);
        Assert.Equal(Original, File.ReadAllBytes(TargetIn(database)));
        Assert.Single(game.GameAssets);
        Assert.Null(game.GameAssets[0].SwappedHash);
    }

    [Fact]
    public async Task ARowFromBeforeSwapsWereRememberedFallsBackToWhatTheScanLastSaw()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        // No swapped hash recorded, and the scan has seen the file as it is now: nothing to ask about.
        var seen = SwappedGame(database, "restore-legacy-seen", onDisk: Edited, swappedHash: null, rowHash: Md5(Edited));
        var result = await seen.ResetDllAsync(GameAssetType.DLSS);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task ARowFromBeforeSwapsWereRememberedStillAsksWhenTheFileOutranTheScan()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        // No swapped hash, and the file differs from what the scan last saw.
        var stale = SwappedGame(database, "restore-legacy-stale", onDisk: Edited, swappedHash: null, rowHash: Md5(Swapped));
        var result = await stale.ResetDllAsync(GameAssetType.DLSS);

        Assert.True(result.NeedsConfirmation);
        Assert.Equal(Edited, File.ReadAllBytes(TargetIn(database)));
    }

    [Fact]
    public async Task TheSwappedHashSurvivesTheDatabase()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var asset = new GameAsset()
        {
            Id = "swapped-hash-persists",
            AssetType = GameAssetType.DLSS,
            Path = TargetIn(database),
            Version = "310.8.0.0",
            Hash = Md5(Swapped),
            SwappedHash = Md5(Swapped),
        };
        await Database.Instance.Connection.InsertAsync(asset);

        var stored = await Database.Instance.Connection.Table<GameAsset>().Where(x => x.Id == asset.Id).FirstOrDefaultAsync();

        Assert.NotNull(stored);
        Assert.Equal(Md5(Swapped), stored.SwappedHash);
    }
}
