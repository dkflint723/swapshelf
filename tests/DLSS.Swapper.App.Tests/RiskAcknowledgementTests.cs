using System;
using System.IO;
using System.Threading.Tasks;
using DLSS_Swapper.Data;
using DLSS_Swapper.Data.ManuallyAdded;
using Xunit;

namespace DLSS_Swapper.App.Tests;

/// <summary>
/// The anti-cheat note is asked per game, remembered, and asked again when there is new information.
/// </summary>
/// <remarks>
/// The old note was one global flag, shown once before the first swap of any game and never again.
/// These pin the replacement: which games are asked, that the answer survives a reload, and that a
/// game which gains an anti-cheat after the answer was given is asked once more - with the name.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public class RiskAcknowledgementTests
{
    static ManuallyAddedGame GameNamed(string id, string title, string installPath)
    {
        return new ManuallyAddedGame(id) { Title = title, InstallPath = installPath };
    }

    [Fact]
    public async Task OnlyGamesNotYetAcknowledgedAreListedOnceEachByTitle()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var done = GameNamed("ack-a", "Zebra", database.GameFolder);
        done.RiskAcknowledgedAt = DateTime.UtcNow;
        var pendingB = GameNamed("ack-b", "Mango", database.GameFolder);
        var pendingC = GameNamed("ack-c", "Apple", database.GameFolder);

        // The same game twice, as a batch with two dlls for one game produces.
        var pending = RiskAcknowledgement.GamesNeedingAcknowledgement(new Game[] { done, pendingB, pendingC, pendingB });

        Assert.Equal(2, pending.Count);
        Assert.Equal("Apple", pending[0].Title);
        Assert.Equal("Mango", pending[1].Title);
    }

    [Fact]
    public async Task AcknowledgingIsRememberedAcrossAReload()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var game = GameNamed("ack-persist", "Persist", database.GameFolder);
        game.AntiCheat = "BattlEye";
        await game.SaveToDatabaseAsync();

        await RiskAcknowledgement.AcknowledgeAsync(new[] { game });

        var reloaded = await Database.Instance.Connection.Table<ManuallyAddedGame>().FirstOrDefaultAsync(x => x.ID == game.ID);
        Assert.NotNull(reloaded);
        Assert.NotNull(reloaded.RiskAcknowledgedAt);
        Assert.Equal("BattlEye", reloaded.AntiCheat);
        Assert.Empty(RiskAcknowledgement.GamesNeedingAcknowledgement(new[] { reloaded }));
    }

    [Fact]
    public async Task ANewlyFoundAntiCheatAsksAgainWithItsName()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var game = GameNamed("ack-new", "Newly Protected", database.GameFolder);
        game.RiskAcknowledgedAt = DateTime.UtcNow;          // answered when there was nothing to name
        Assert.Null(game.AntiCheat);

        game.RecordAntiCheat(new[] { Path.Combine(database.GameFolder, "EasyAntiCheat", "EasyAntiCheat_x64.dll") });

        Assert.Equal("Easy Anti-Cheat", game.AntiCheat);
        Assert.Null(game.RiskAcknowledgedAt);
        Assert.Single(RiskAcknowledgement.GamesNeedingAcknowledgement(new[] { game }));
    }

    [Fact]
    public async Task AnAntiCheatAlreadyKnownDoesNotKeepAsking()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var game = GameNamed("ack-known", "Already Known", database.GameFolder);
        game.AntiCheat = "BattlEye";
        var answered = DateTime.UtcNow;
        game.RiskAcknowledgedAt = answered;

        game.RecordAntiCheat(new[] { Path.Combine(database.GameFolder, "BattlEye", "BEClient_x64.dll") });

        Assert.Equal("BattlEye", game.AntiCheat);
        Assert.Equal(answered, game.RiskAcknowledgedAt);
    }

    [Fact]
    public async Task AGameWithNoMarkersKeepsItsAnswer()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        var game = GameNamed("ack-plain", "Plain", database.GameFolder);
        var answered = DateTime.UtcNow;
        game.RiskAcknowledgedAt = answered;

        game.RecordAntiCheat(new[] { Path.Combine(database.GameFolder, "nvngx_dlss.dll") });

        Assert.Null(game.AntiCheat);
        Assert.Equal(answered, game.RiskAcknowledgedAt);
    }

    [Fact]
    public async Task TopLevelFoldersCountAsMarkersToo()
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        Directory.CreateDirectory(Path.Combine(database.GameFolder, "EasyAntiCheat"));
        var game = GameNamed("ack-folder", "Folder Only", database.GameFolder);

        // No anti-cheat dll among the enumerated paths; the folder alone says it.
        game.RecordAntiCheat(new[] { Path.Combine(database.GameFolder, "nvngx_dlss.dll") });

        Assert.Equal("Easy Anti-Cheat", game.AntiCheat);
    }
}
