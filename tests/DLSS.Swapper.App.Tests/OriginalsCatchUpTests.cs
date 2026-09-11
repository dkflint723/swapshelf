using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using DLSS_Swapper.Data;
using DLSS_Swapper.Data.ManuallyAdded;
using Xunit;

namespace DLSS_Swapper.App.Tests;

/// <summary>
/// Every saved original gets its library copy, not only the ones saved on first sight of a game.
/// </summary>
/// <remarks>
/// <para>
/// The mirror was made in one place: when a game was first found. Originals saved before the mirror
/// existed - nearly all of an existing library - and originals the executor saved at swap time went
/// without, so the protection was missing where it mattered most. These pin the catch-up, the swap
/// path, and the two refusals that keep an unattended bulk copy safe: a file that is not the one its
/// row records, and a disk that would be left nearly full.
/// </para>
/// </remarks>
[Collection(DatabaseCollection.Name)]
public class OriginalsCatchUpTests : IDisposable
{
    readonly Func<string, long?> _originalProbe = OriginalsStore.FreeSpaceProbe;
    readonly Func<string, bool> _originalRunningCheck = Game.IsRunningCheck;

    public void Dispose()
    {
        OriginalsStore.FreeSpaceProbe = _originalProbe;
        Game.IsRunningCheck = _originalRunningCheck;
    }

    static string Md5(byte[] bytes) => Convert.ToHexString(MD5.HashData(bytes));

    /// <summary>A game with a dll and a saved original beside it, recorded the way a scan records them.</summary>
    static ManuallyAddedGame GameWithASavedOriginal(TemporaryDatabase database, string id, GameAssetType assetType = GameAssetType.DLSS, string fileName = "nvngx_dlss.dll", string? recordedHash = null)
    {
        var source = Path.Combine(database.GameFolder, id, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4 });

        var original = new byte[4096];
        new Random(id.GetHashCode()).NextBytes(original);
        File.WriteAllBytes(source + ".dlsss", original);

        var game = new ManuallyAddedGame(id) { Title = id, InstallPath = Path.GetDirectoryName(source)! };
        game.GameAssets.Add(new GameAsset() { Id = id, AssetType = assetType, Path = source, Version = "310.2.1.0", Hash = Md5(new byte[] { 1, 2, 3, 4 }) });
        game.GameAssets.Add(new GameAsset()
        {
            Id = id,
            AssetType = DLLManager.Instance.GetAssetBackupType(assetType),
            Path = source + ".dlsss",
            Version = "310.1.0.0",
            Hash = recordedHash ?? Md5(original),
        });
        return game;
    }

    static string SourceOf(Game game) => game.GameAssets[0].Path;

    [Fact]
    public async Task AnOriginalSavedBeforeTheMirrorExistedGetsItsCopyOnce()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        OriginalsStore.FreeSpaceProbe = _ => null;
        var game = GameWithASavedOriginal(database, "catchup-once");

        Assert.False(OriginalsStore.HasMirror(game.ID, SourceOf(game)));
        Assert.Equal(1, OriginalsStore.CatchUp(new[] { game }));
        Assert.True(OriginalsStore.HasMirror(game.ID, SourceOf(game)));

        // Caught up: a second pass copies nothing.
        Assert.Equal(0, OriginalsStore.CatchUp(new[] { game }));

        // And the copy is the one that comes back when the game folder loses its own.
        var backupPath = SourceOf(game) + ".dlsss";
        var original = File.ReadAllBytes(backupPath);
        File.Delete(backupPath);
        Assert.True(OriginalsStore.TryRestoreBackup(game.ID, SourceOf(game), backupPath));
        Assert.Equal(original, File.ReadAllBytes(backupPath));
    }

    [Fact]
    public async Task AnOriginalThatIsNotTheFileItsRowRecordsIsNotCopied()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        OriginalsStore.FreeSpaceProbe = _ => null;
        var game = GameWithASavedOriginal(database, "catchup-mismatch", recordedHash: "00000000000000000000000000000000");

        Assert.Equal(0, OriginalsStore.CatchUp(new[] { game }));
        Assert.False(OriginalsStore.HasMirror(game.ID, SourceOf(game)));
    }

    [Fact]
    public async Task ANearlyFullDiskStopsTheCatchUpBeforeAnythingIsCopied()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var asked = 0;
        OriginalsStore.FreeSpaceProbe = _ => { asked++; return 100L * 1024 * 1024; };

        var first = GameWithASavedOriginal(database, "catchup-space-1");
        var second = GameWithASavedOriginal(database, "catchup-space-2");

        Assert.Equal(0, OriginalsStore.CatchUp(new List<Game>() { first, second }));
        Assert.False(OriginalsStore.HasMirror(first.ID, SourceOf(first)));
        Assert.False(OriginalsStore.HasMirror(second.ID, SourceOf(second)));

        // Stopped at the first refusal rather than being refused, and logging it, for every file.
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task EnoughSpaceIsMeasuredAfterTheCopyNotBefore()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var game = GameWithASavedOriginal(database, "catchup-space-edge");
        var size = new FileInfo(SourceOf(game) + ".dlsss").Length;

        // Exactly the reserve free now: the copy would dip under it, so it is refused.
        OriginalsStore.FreeSpaceProbe = _ => OriginalsStore.MinimumFreeSpaceBytes;
        Assert.Equal(0, OriginalsStore.CatchUp(new[] { game }));

        // The reserve plus the file: the copy leaves exactly the reserve, which is allowed.
        OriginalsStore.FreeSpaceProbe = _ => OriginalsStore.MinimumFreeSpaceBytes + size;
        Assert.Equal(1, OriginalsStore.CatchUp(new[] { game }));
    }

    [Fact]
    public async Task TurnedOffMeansNothingIsCopied()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        OriginalsStore.FreeSpaceProbe = _ => null;
        var game = GameWithASavedOriginal(database, "catchup-off");

        var before = Settings.Instance.KeepOriginalCopiesInLibrary;
        try
        {
            Settings.Instance.KeepOriginalCopiesInLibrary = false;
            Assert.Equal(0, OriginalsStore.CatchUp(new[] { game }));
            Assert.False(OriginalsStore.HasMirror(game.ID, SourceOf(game)));
        }
        finally
        {
            Settings.Instance.KeepOriginalCopiesInLibrary = before;
        }
    }

    [Fact]
    public async Task ADllNoGameShipsHasNoOriginalToCopy()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        OriginalsStore.FreeSpaceProbe = _ => null;
        var game = GameWithASavedOriginal(database, "catchup-nr", GameAssetType.DLSS_NR, "nvngx_dlssnr.dll");

        Assert.Equal(0, OriginalsStore.CatchUp(new[] { game }));
    }

    [Fact]
    public async Task AnOriginalSavedAtSwapTimeIsCopiedToo()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        using var manifest = new ManifestScope();
        OriginalsStore.FreeSpaceProbe = _ => null;
        Game.IsRunningCheck = _ => false;

        // The game's dll, with no saved original beside it yet: the swap is what saves one.
        var original = new byte[] { 10, 20, 30, 40, 50 };
        var target = Path.Combine(database.GameFolder, "nvngx_dlss.dll");
        File.WriteAllBytes(target, original);

        var incoming = new byte[] { 99, 98, 97, 96 };
        var libraryDll = Path.Combine(database.Root, "library", "nvngx_dlss.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(libraryDll)!);
        File.WriteAllBytes(libraryDll, incoming);

        var game = new ManuallyAddedGame("catchup-swap") { Title = "Swap Time", InstallPath = database.GameFolder };
        game.GameAssets.Add(new GameAsset() { Id = game.ID, AssetType = GameAssetType.DLSS, Path = target, Version = "3.1.0.0", Hash = Md5(original) });

        var record = new DLLRecord()
        {
            AssetType = GameAssetType.DLSS,
            Version = "3.7.20.0",
            MD5Hash = Md5(incoming),
            LocalRecord = LocalRecord.FromExpectedPath(libraryDll),
        };

        // The fake file carries no signature, and this test is about what happens after the checks.
        var allowUntrusted = Settings.Instance.AllowUntrusted;
        try
        {
            Settings.Instance.AllowUntrusted = true;

            var result = await game.UpdateDllAsync(record);
            Assert.True(result.Success, result.Message);
        }
        finally
        {
            Settings.Instance.AllowUntrusted = allowUntrusted;
        }

        Assert.Equal(incoming, File.ReadAllBytes(target));
        Assert.Equal(original, File.ReadAllBytes(target + ".dlsss"));

        Assert.True(OriginalsStore.HasMirror(game.ID, target));
        var (copy, _) = OriginalsStore.LocationFor(game.ID, target);
        Assert.Equal(original, File.ReadAllBytes(copy));
    }
}
