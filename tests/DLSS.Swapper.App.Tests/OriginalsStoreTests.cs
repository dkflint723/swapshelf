using System.IO;
using System.Threading.Tasks;
using DLSS_Swapper;
using DLSS_Swapper.Data;
using Xunit;

namespace DLSS_Swapper.App.Tests;

/// <summary>
/// The second copy of each saved original, and getting the first copy back from it.
/// </summary>
/// <remarks>
/// The .dlsss beside a game's dll is the only copy of what the game shipped with, and it lives in
/// a folder the game owns. Steam's verify, a launcher repair, or a patcher pruning unknown files
/// takes it with everything else. These pin that the mirror holds a faithful copy, that it comes
/// back when the .dlsss is gone, and that a mirror which no longer matches its own record is
/// refused rather than trusted.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public class OriginalsStoreTests
{
    static string WriteBytes(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public async Task ASavedOriginalIsMirroredAndComesBackWhenTheBackupIsGone()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var source = Path.Combine(database.GameFolder, "nvngx_dlss.dll");
        var backup = source + ".dlsss";
        WriteBytes(source, "the dll as shipped");
        WriteBytes(backup, "the dll as shipped");

        Assert.True(OriginalsStore.Mirror("mirror-1", source, backup, "310.1.0.0"));

        var (copy, sidecar) = OriginalsStore.LocationFor("mirror-1", source);
        Assert.True(File.Exists(copy));
        Assert.True(File.Exists(sidecar));
        Assert.Equal("the dll as shipped", File.ReadAllText(copy));

        // The game update happens.
        File.Delete(backup);
        Assert.False(File.Exists(backup));

        Assert.True(OriginalsStore.TryRestoreBackup("mirror-1", source, backup));
        Assert.Equal("the dll as shipped", File.ReadAllText(backup));
    }

    [Fact]
    public async Task AMirrorThatNoLongerMatchesItsRecordIsRefused()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var source = Path.Combine(database.GameFolder, "nvngx_dlss.dll");
        var backup = source + ".dlsss";
        WriteBytes(backup, "the dll as shipped");
        Assert.True(OriginalsStore.Mirror("mirror-2", source, backup, "310.1.0.0"));

        // Something rewrites the mirror behind the app's back.
        var (copy, _) = OriginalsStore.LocationFor("mirror-2", source);
        File.WriteAllText(copy, "not what was saved");
        File.Delete(backup);

        Assert.False(OriginalsStore.TryRestoreBackup("mirror-2", source, backup));
        Assert.False(File.Exists(backup));
    }

    [Fact]
    public async Task TwoLocationsOfTheSameDllInOneGameDoNotShareAMirror()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var first = Path.Combine(database.GameFolder, "nvngx_dlss.dll");
        var second = Path.Combine(database.GameFolder, "bin", "nvngx_dlss.dll");
        WriteBytes(first + ".dlsss", "first copy");
        WriteBytes(second + ".dlsss", "second copy");

        Assert.True(OriginalsStore.Mirror("mirror-3", first, first + ".dlsss", "1"));
        Assert.True(OriginalsStore.Mirror("mirror-3", second, second + ".dlsss", "1"));

        var (copyA, _) = OriginalsStore.LocationFor("mirror-3", first);
        var (copyB, _) = OriginalsStore.LocationFor("mirror-3", second);
        Assert.NotEqual(copyA, copyB);
        Assert.Equal("first copy", File.ReadAllText(copyA));
        Assert.Equal("second copy", File.ReadAllText(copyB));
    }

    [Fact]
    public async Task NoMirrorMeansNoRestoreAndNoException()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var source = Path.Combine(database.GameFolder, "nvngx_dlss.dll");

        Assert.False(OriginalsStore.TryRestoreBackup("mirror-4", source, source + ".dlsss"));
        Assert.Null(OriginalsStore.RecordedHash("mirror-4", source));
    }

    [Fact]
    public async Task TheSidecarRecordsWhatWasSavedAndItsHash()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var source = Path.Combine(database.GameFolder, "nvngx_dlss.dll");
        var backup = source + ".dlsss";
        WriteBytes(backup, "the dll as shipped");
        OriginalsStore.Mirror("mirror-5", source, backup, "310.1.0.0");

        var recorded = OriginalsStore.RecordedHash("mirror-5", source);
        Assert.False(string.IsNullOrWhiteSpace(recorded));
        Assert.Equal(32, recorded!.Length);

        var (_, sidecar) = OriginalsStore.LocationFor("mirror-5", source);
        var text = File.ReadAllText(sidecar);
        Assert.Contains("310.1.0.0", text);
        Assert.Contains("nvngx_dlss.dll", text);
        Assert.Contains(recorded, text);
    }

    [Fact]
    public async Task TheSettingTurnsTheMirrorOff()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var before = Settings.Instance.KeepOriginalCopiesInLibrary;
        try
        {
            Settings.Instance.KeepOriginalCopiesInLibrary = false;

            var source = Path.Combine(database.GameFolder, "nvngx_dlss.dll");
            var backup = source + ".dlsss";
            WriteBytes(backup, "the dll as shipped");

            Assert.False(OriginalsStore.Mirror("mirror-6", source, backup, "1"));
            var (copy, _) = OriginalsStore.LocationFor("mirror-6", source);
            Assert.False(File.Exists(copy));
        }
        finally
        {
            Settings.Instance.KeepOriginalCopiesInLibrary = before;
        }
    }

    [Fact]
    public async Task TheMirrorLivesUnderTheLibraryNotTheGame()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var source = Path.Combine(database.GameFolder, "nvngx_dlss.dll");
        var (copy, _) = OriginalsStore.LocationFor("mirror-7", source);

        Assert.StartsWith(Storage.GetOriginalsFolder(), copy);
        Assert.False(copy.StartsWith(database.GameFolder));
    }
}
