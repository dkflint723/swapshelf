using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using DLSS_Swapper.Data;
using DLSS_Swapper.Data.ManuallyAdded;
using DLSS_Swapper.Helpers;
using Xunit;

namespace DLSS_Swapper.App.Tests;

/// <summary>
/// SHA-256 is recorded beside MD5 and checked wherever both sides have one.
/// </summary>
/// <remarks>
/// MD5 stays the identity the manifest and the history are keyed by, so nothing here removes a
/// check; each test is about the second digest being kept, compared, and learned.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public class Sha256AlongsideMd5Tests
{
    static string Md5(byte[] bytes) => Convert.ToHexString(MD5.HashData(bytes));
    static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    [Fact]
    public async Task AScannedDllCarriesBothDigestsAndTheyReachTheDatabase()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var path = database.WriteFakeDll("nvngx_dlss.dll");
        var bytes = File.ReadAllBytes(path);

        var asset = new GameAsset() { Id = "sha-scan", AssetType = GameAssetType.DLSS, Path = path };
        asset.LoadVersionAndHash();

        Assert.Equal(Md5(bytes), asset.Hash);
        Assert.Equal(Sha256(bytes), asset.Sha256);

        await Database.Instance.Connection.InsertAsync(asset);
        var stored = await Database.Instance.Connection.Table<GameAsset>().Where(x => x.Id == asset.Id).FirstOrDefaultAsync();

        Assert.NotNull(stored);
        Assert.Equal(Sha256(bytes), stored.Sha256);
    }

    [Fact]
    public async Task ARowWithoutASha256IsReadAgainOnceRatherThanTrusted()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var path = database.WriteFakeDll("nvngx_dlss.dll");

        var fresh = new GameAsset() { Id = "sha-cache", AssetType = GameAssetType.DLSS, Path = path };
        fresh.LoadVersionAndSize();

        var cachedWithoutSha256 = new GameAsset() { Id = "sha-cache", AssetType = GameAssetType.DLSS, Path = path, Version = fresh.Version, Size = fresh.Size, Hash = "SOMETHING" };
        Assert.False(fresh.MatchesCachedFile(cachedWithoutSha256));

        var cachedWithBoth = new GameAsset() { Id = "sha-cache", AssetType = GameAssetType.DLSS, Path = path, Version = fresh.Version, Size = fresh.Size, Hash = "SOMETHING", Sha256 = "SOMETHING ELSE" };
        Assert.True(fresh.MatchesCachedFile(cachedWithBoth));
    }

    [Fact]
    public async Task TheLocalStoreRemembersAcrossAReloadAndFillsInManifestRecords()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        LocalDigestStore.Reload();

        LocalDigestStore.Remember("aabb", "CCDD");
        Assert.Equal("CCDD", LocalDigestStore.TryGet("AABB"));

        LocalDigestStore.Reload();
        Assert.Equal("CCDD", LocalDigestStore.TryGet("aabb"));

        var manifestRecord = new DLLRecord() { AssetType = GameAssetType.DLSS, Version = "3.7.20.0", MD5Hash = "AABB" };
        var recordWithItsOwn = new DLLRecord() { AssetType = GameAssetType.DLSS, Version = "3.7.20.0", MD5Hash = "AABB", Sha256Hash = "OWN" };
        LocalDigestStore.Apply(new[] { manifestRecord, recordWithItsOwn });

        Assert.Equal("CCDD", manifestRecord.Sha256Hash);
        Assert.Equal("OWN", recordWithItsOwn.Sha256Hash);
        Assert.Null(LocalDigestStore.TryGet("unknown"));
    }

    [Fact]
    public async Task ASwapRefusesAFileWhoseSha256DoesNotMatchTheRecordEvenWhenTheMd5Does()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var library = database.WriteFakeDll("library-nvngx_dlss.dll");
        var bytes = File.ReadAllBytes(library);

        var game = new ManuallyAddedGame("sha-swap-wrong") { Title = "Sha Swap", InstallPath = database.GameFolder };
        game.GameAssets.Add(new GameAsset() { Id = game.ID, AssetType = GameAssetType.DLSS, Path = Path.Combine(database.GameFolder, "nvngx_dlss.dll"), Version = "3.1.0.0", Hash = "OLD" });

        var record = new DLLRecord()
        {
            AssetType = GameAssetType.DLSS,
            Version = "3.7.20.0",
            MD5Hash = Md5(bytes),
            Sha256Hash = "NOT THE SHA256 OF THAT FILE",
            LocalRecord = LocalRecord.FromExpectedPath(library),
        };

        var result = await game.UpdateDllAsync(record);

        Assert.False(result.Success);
        Assert.Equal(ResourceHelper.GetString("Game_Swap_InvalidHash"), result.Message);
    }

    [Fact]
    public async Task ASwapLearnsTheSha256OfARecordThatHadNone()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        LocalDigestStore.Reload();
        var library = database.WriteFakeDll("library-nvngx_dlss.dll");
        var bytes = File.ReadAllBytes(library);

        var game = new ManuallyAddedGame("sha-swap-learn") { Title = "Sha Learn", InstallPath = database.GameFolder };
        game.GameAssets.Add(new GameAsset() { Id = game.ID, AssetType = GameAssetType.DLSS, Path = Path.Combine(database.GameFolder, "nvngx_dlss.dll"), Version = "3.1.0.0", Hash = "OLD" });

        var record = new DLLRecord()
        {
            AssetType = GameAssetType.DLSS,
            Version = "3.7.20.0",
            MD5Hash = Md5(bytes),
            LocalRecord = LocalRecord.FromExpectedPath(library),
        };

        var result = await game.UpdateDllAsync(record);

        // Past the hash gate: the next refusal is about the fake file's signature, not its hash.
        Assert.False(result.Success);
        Assert.NotEqual(ResourceHelper.GetString("Game_Swap_InvalidHash"), result.Message);

        Assert.Equal(Sha256(bytes), record.Sha256Hash);
        Assert.Equal(Sha256(bytes), LocalDigestStore.TryGet(Md5(bytes)));
    }
}
