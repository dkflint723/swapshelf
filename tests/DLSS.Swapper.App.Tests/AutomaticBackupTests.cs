using System.IO;
using System.Threading.Tasks;
using DLSS_Swapper.Data;
using Xunit;

namespace DLSS_Swapper.App.Tests;

/// <summary>
/// Covers the rule automatic backups rest on: an existing saved original is never replaced.
/// </summary>
/// <remarks>
/// Backups used to be taken only the first time a game was seen, so a dll found later was detected
/// and left unprotected. Widening that to every newly found dll is only safe because of this rule:
/// without it, backing up a dll that had already been swapped would promote the swapped version to
/// "original" and destroy the only copy of the real one.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public class AutomaticBackupTests
{
    static GameAsset Asset(string gameId, GameAssetType assetType, string path)
    {
        return new GameAsset()
        {
            Id = gameId,
            AssetType = assetType,
            Path = path,
            Version = "310.1.0.0",
            Size = 2048,
            Hash = string.Empty,
        };
    }

    [Fact]
    public async Task ADllWithNoCopyGetsOne()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        using var manifest = new ManifestScope();

        var dllPath = database.WriteFakeDll("nvngx_dlss.dll");
        var game = new TestGame("backup_1");
        game.GameAssets.Add(Asset(game.ID, GameAssetType.DLSS, dllPath));

        var saved = await game.SaveOriginalCopiesAsync();

        Assert.Equal(1, saved);
        Assert.True(File.Exists(dllPath + ".dlsss"));
    }

    [Fact]
    public async Task AnExistingCopyIsNeverReplaced()
    {
        // The rule the whole change rests on. If a swapped dll could overwrite the saved original,
        // the user would lose the only copy of the file they actually shipped with.
        await using var database = await TemporaryDatabase.CreateAsync();
        using var manifest = new ManifestScope();

        var dllPath = database.WriteFakeDll("nvngx_dlss.dll");
        var game = new TestGame("backup_2");
        game.GameAssets.Add(Asset(game.ID, GameAssetType.DLSS, dllPath));
        await game.SaveOriginalCopiesAsync();

        var originalBytes = File.ReadAllBytes(dllPath + ".dlsss");

        // The installed dll is replaced, as a swap would do, and a backup is attempted again.
        File.WriteAllBytes(dllPath, new byte[9999]);
        await game.SaveOriginalCopiesAsync();

        Assert.Equal(originalBytes, File.ReadAllBytes(dllPath + ".dlsss"));
    }

    [Fact]
    public async Task OnlyTheDllsWithoutACopyAreBackedUp()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        using var manifest = new ManifestScope();

        var dlssPath = database.WriteFakeDll("nvngx_dlss.dll");
        var xessPath = database.WriteFakeDll("libxess.dll");

        var game = new TestGame("backup_3");
        game.GameAssets.Add(Asset(game.ID, GameAssetType.DLSS, dlssPath));
        await game.SaveOriginalCopiesAsync();

        // XeSS arrives later, as a patch would deliver it.
        game.GameAssets.Add(Asset(game.ID, GameAssetType.XeSS, xessPath));
        var saved = await game.SaveOriginalCopiesAsync();

        Assert.Equal(1, saved);
        Assert.True(File.Exists(xessPath + ".dlsss"));
    }

    /// <summary>
    /// A game shipping the same dll in two folders needs a copy of both.
    /// </summary>
    /// <remarks>
    /// The gate used to ask whether any dll of the same TYPE had a backup, so one copy answered for
    /// every location: the first was saved, the second skipped, and the method reported success.
    /// The row then read as protected while that second location had no original saved anywhere.
    /// </remarks>
    [Fact]
    public async Task EveryLocationOfADllGetsItsOwnCopy()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        using var manifest = new ManifestScope();

        var firstPath = database.WriteFakeDll("nvngx_dlss.dll");

        var engineFolder = Path.Combine(database.GameFolder, "Engine", "Binaries");
        Directory.CreateDirectory(engineFolder);
        var secondPath = Path.Combine(engineFolder, "nvngx_dlss.dll");
        File.Copy(firstPath, secondPath);

        var game = new TestGame("backup_two_locations");
        game.GameAssets.Add(Asset(game.ID, GameAssetType.DLSS, firstPath));
        game.GameAssets.Add(Asset(game.ID, GameAssetType.DLSS, secondPath));

        var saved = await game.SaveOriginalCopiesAsync();

        Assert.Equal(2, saved);
        Assert.True(File.Exists(firstPath + ".dlsss"));
        Assert.True(File.Exists(secondPath + ".dlsss"));
    }

    /// <summary>
    /// And the row has to say so while only one of the two is covered.
    /// </summary>
    /// <remarks>
    /// The same type-wide question was asked in three places at once, so the list, the row and the
    /// sidebar all agreed a half protected game was fully protected. They read one rule now.
    /// </remarks>
    [Fact]
    public async Task AGameWithOnlyOneOfTwoLocationsCoveredStillReportsAMissingCopy()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        using var manifest = new ManifestScope();

        var firstPath = database.WriteFakeDll("nvngx_dlss.dll");

        var engineFolder = Path.Combine(database.GameFolder, "Engine", "Binaries");
        Directory.CreateDirectory(engineFolder);
        var secondPath = Path.Combine(engineFolder, "nvngx_dlss.dll");
        File.Copy(firstPath, secondPath);

        var game = new TestGame("backup_half_covered");
        var first = Asset(game.ID, GameAssetType.DLSS, firstPath);
        var second = Asset(game.ID, GameAssetType.DLSS, secondPath);
        game.GameAssets.Add(first);
        game.GameAssets.Add(second);

        // Only the first location has its original saved.
        game.GameAssets.Add(Asset(game.ID, GameAssetType.DLSS_BACKUP, firstPath + ".dlsss"));

        Assert.True(game.HasSavedOriginal(first));
        Assert.False(game.HasSavedOriginal(second));
        Assert.True(GameFilters.IsMissingABackup(game));
    }

    /// <summary>
    /// A scan that removes a stale backup must not write a new one from the dll on disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scan used to delete the saved original when the installed dll no longer matched the
    /// version it recorded, on the grounds that a game updated past the version you swapped to
    /// should not read as a downgrade. That destroyed the only copy of the file the game shipped
    /// with - the thing the whole app exists to be able to put back - to avoid a display detail,
    /// and no rescan or reinstall brings it back. It cost 29 saved originals across 13 games in a
    /// single scan of one real library.
    /// </para>
    /// <para>
    /// The copy stays now, and the confusion is answered with words instead: every surface that
    /// offers a restore names both versions first. What this test kept from that era is the other
    /// half, which matters more than ever now the file survives - the automatic backup must not
    /// write a new "original" over it from whatever is installed at the time, or a swap this app
    /// made and failed to record would be promoted to "what the game shipped with".
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AScanKeepsTheSavedOriginalAndNeverOverwritesItWithTheInstalledDll()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        using var manifest = new ManifestScope();

        var dllPath = database.WriteFakeDll("nvngx_dlss.dll");
        var backupPath = dllPath + ".dlsss";

        // What the game shipped with, saved.
        File.Copy(dllPath, backupPath);
        var shippedBytes = File.ReadAllBytes(backupPath);

        // Something replaced the installed dll since the last scan, and the recorded version no
        // longer matches what is on disk. A swap this app made and did not get to record looks
        // exactly like this.
        var swappedBytes = new byte[4096];
        for (var index = 0; index < swappedBytes.Length; index += 1)
        {
            swappedBytes[index] = 0x5A;
        }

        File.WriteAllBytes(dllPath, swappedBytes);

        var game = new TestGame("backup_stale_scan")
        {
            InstallPath = database.GameFolder,

            // Keeps the scan off the cover art path, which has nothing to do with this.
            IsHidden = true,
        };

        game.GameAssets.Add(Asset(game.ID, GameAssetType.DLSS, dllPath));

        game.ProcessGame(autoSave: false);
        await WaitForScanAsync(game);

        // Worth stating: the setup is only meaningful because these differ.
        Assert.NotEqual(shippedBytes, swappedBytes);

        // The copy the user cannot recreate is still there...
        Assert.True(File.Exists(backupPath));

        // ...and it still holds what the game shipped with, not the file that replaced it. Byte
        // for byte, because "a copy exists" would pass even if the scan had overwritten it.
        Assert.Equal(shippedBytes, File.ReadAllBytes(backupPath));
    }

    /// <summary>
    /// A dll replaced outside the app is recorded as that, and nothing is destroyed over it.
    /// </summary>
    /// <remarks>
    /// The record is what a later scan needs to tell "your swap was undone" from "this dll is new
    /// to me", and what <c>UndoneSwapFinder</c> reads to say so on the games page. It used to be
    /// written alongside a note that the saved original had been deleted; there is no deletion to
    /// report now, so this asserts both halves at once - the change is remembered, and the copy
    /// survives it.
    /// </remarks>
    [Fact]
    public async Task ADllReplacedOutsideTheAppIsRecordedAndTheOriginalSurvives()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        using var manifest = new ManifestScope();

        var dllPath = database.WriteFakeDll("nvngx_dlss.dll");
        var backupPath = dllPath + ".dlsss";
        File.Copy(dllPath, backupPath);

        // The installed dll no longer matches what was recorded: a game update, or a tool that
        // writes dlls into game folders, has been through since the last scan.
        File.WriteAllBytes(dllPath, new byte[4096]);

        var game = new TestGame("backup_history")
        {
            InstallPath = database.GameFolder,
            IsHidden = true,
        };

        game.GameAssets.Add(Asset(game.ID, GameAssetType.DLSS, dllPath));

        game.ProcessGame(autoSave: false);
        await WaitForScanAsync(game);

        Assert.True(File.Exists(backupPath));

        var history = await Database.Instance.Connection.Table<GameHistory>()
            .Where(x => x.GameId == game.ID)
            .ToListAsync();

        Assert.Contains(history, x => x.EventType == GameHistoryEventType.DLLChangedExternally);

        // Nothing was destroyed, so nothing should claim it was.
        Assert.DoesNotContain(history, x => x.EventType == GameHistoryEventType.DLLBackupRemoved);
    }

    /// <summary>
    /// ProcessGame reports through a flag rather than a task, so the test waits on the flag.
    /// </summary>
    static async Task WaitForScanAsync(Game game)
    {
        for (var attempt = 0; attempt < 200; attempt += 1)
        {
            if (game.Processing == false)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("The scan did not finish.");
    }

    [Fact]
    public async Task ACopyIsAFaithfulCopy()
    {
        // It is the file the user gets back when they revert, so it has to be byte identical.
        await using var database = await TemporaryDatabase.CreateAsync();
        using var manifest = new ManifestScope();

        var dllPath = database.WriteFakeDll("nvngx_dlss.dll", bytes: 4096);
        var game = new TestGame("backup_4");
        game.GameAssets.Add(Asset(game.ID, GameAssetType.DLSS, dllPath));

        await game.SaveOriginalCopiesAsync();

        Assert.Equal(File.ReadAllBytes(dllPath), File.ReadAllBytes(dllPath + ".dlsss"));
    }

    /// <summary>
    /// A dll no game ships gets no "original", because there is not one to save.
    /// </summary>
    /// <remarks>
    /// The backup rule reads the file sitting in a game folder as the version the developer
    /// shipped, which is true of every released upscaler and false of a dll that only got there
    /// because somebody installed it. Copying it anyway records the installed version as the
    /// original, offers to "restore" a game to a file it never had, and spends a second copy of a
    /// 158 MB dll per location doing it.
    /// </remarks>
    [Fact]
    public async Task ADllNoGameShipsGetsNoFabricatedOriginal()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        using var manifest = new ManifestScope();

        var dllPath = database.WriteFakeDll("nvngx_dlssnr.dll");
        var game = new TestGame("backup_not_shipped");
        game.GameAssets.Add(Asset(game.ID, GameAssetType.DLSS_NR, dllPath));

        var saved = await game.SaveOriginalCopiesAsync();

        Assert.Equal(0, saved);
        Assert.False(File.Exists(dllPath + ".dlsss"));
    }

    /// <summary>
    /// And it is not reported as missing one either, on any surface.
    /// </summary>
    /// <remarks>
    /// Every count of backup coverage in the app - the row sentence, the games list, the "Missing a
    /// saved original" tab, the sidebar - goes through <c>Game.HasSavedOriginal</c>. Answering
    /// false here would put a warning on five games asking the user to fix something that cannot be
    /// fixed and is not broken.
    /// </remarks>
    [Fact]
    public async Task ADllNoGameShipsIsNotCountedAsMissingAnOriginal()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        using var manifest = new ManifestScope();

        var notShipped = database.WriteFakeDll("nvngx_dlssnr.dll");
        var game = new TestGame("backup_not_shipped_2");
        game.GameAssets.Add(Asset(game.ID, GameAssetType.DLSS_NR, notShipped));

        Assert.False(GameFilters.IsMissingABackup(game));

        // A released dll in the same game still answers for itself, so this is an exemption for one
        // type rather than a hole in the coverage rule.
        var shipped = database.WriteFakeDll("nvngx_dlss.dll");
        game.GameAssets.Add(Asset(game.ID, GameAssetType.DLSS, shipped));

        Assert.True(GameFilters.IsMissingABackup(game));
    }
}
