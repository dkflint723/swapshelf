using System.Collections.Generic;
using DLSS_Swapper.Data;
using DLSS_Swapper.Data.Steam;
using DLSS_Swapper.Data.UbisoftConnect;
using Xunit;

namespace DLSS_Swapper.App.Tests;

/// <summary>
/// When two Game objects are the same game.
/// </summary>
/// <remarks>
/// Platform ids are not unique across launchers: Steam app ids, Ubisoft Connect install ids, GOG
/// and EA ids are all bare numbers from overlapping ranges. Equals used to match on the platform id
/// alone, and the library list is a plain List, so AddGame's Contains found the first game with
/// that number and handed it back - the second game never entered the library, and the first had
/// its title and install path overwritten with the other game's and saved under its own id.
/// </remarks>
public class GameIdentityTests
{
    [Fact]
    public void TwoGamesFromDifferentLaunchersSharingAPlatformIdAreNotTheSameGame()
    {
        var steam = new SteamGame("720") { Title = "A Steam game" };
        var ubisoft = new UbisoftConnectGame("720") { Title = "A Ubisoft game" };

        Assert.NotEqual(steam.ID, ubisoft.ID);
        Assert.False(steam.Equals(ubisoft));
        Assert.False(ubisoft.Equals(steam));
    }

    /// <summary>
    /// The consequence that mattered: a list holding one must not report holding the other.
    /// </summary>
    [Fact]
    public void AListHoldingOneDoesNotContainTheOther()
    {
        var steam = new SteamGame("720") { Title = "A Steam game" };
        var ubisoft = new UbisoftConnectGame("720") { Title = "A Ubisoft game" };

        var games = new List<Game>() { steam };

        Assert.DoesNotContain(ubisoft, games);
    }

    [Fact]
    public void TheSameGameFromTheSameLauncherIsStillTheSameGame()
    {
        var first = new SteamGame("720") { Title = "A Steam game" };
        var second = new SteamGame("720") { Title = "The same game, rescanned" };

        Assert.True(first.Equals(second));
    }
}
