using DLSS_Swapper.Compatibility;
using Xunit;

namespace DLSS_Swapper.Tests;

/// <summary>
/// Naming the anti-cheat a game carries, from paths the scan already has.
/// </summary>
/// <remarks>
/// Half of these are about what is NOT matched. A false positive here is a warning the user learns
/// to click through, which is worse than no warning.
/// </remarks>
public class AntiCheatMarkersTests
{
    [Fact]
    public void EasyAntiCheatIsRecognisedByItsFolder()
    {
        var result = AntiCheatMarkers.Detect(new[]
        {
            @"EasyAntiCheat\EasyAntiCheat_x64.dll",
            @"nvngx_dlss.dll",
        });

        Assert.Equal("Easy Anti-Cheat", result);
    }

    [Fact]
    public void BattlEyeIsRecognisedByItsClient()
    {
        Assert.Equal("BattlEye", AntiCheatMarkers.Detect(new[] { @"BattlEye\BEClient_x64.dll" }));
        Assert.Equal("BattlEye", AntiCheatMarkers.Detect(new[] { "BEService_x64.exe" }));
    }

    [Fact]
    public void CaseAndSeparatorDoNotMatter()
    {
        Assert.Equal("Easy Anti-Cheat", AntiCheatMarkers.Detect(new[] { "EASYANTICHEAT/easyanticheat_x64.DLL" }));
        Assert.Equal("GameGuard", AntiCheatMarkers.Detect(new[] { @"GAMEGUARD\npgg.dll" }));
    }

    [Fact]
    public void AGameWithOnlyUpscalerDllsHasNone()
    {
        var result = AntiCheatMarkers.Detect(new[]
        {
            @"nvngx_dlss.dll",
            @"nvngx_dlssg.dll",
            @"amd_fidelityfx_dx12.dll",
            @"libxess.dll",
            @"bin\x64\nvngx_dlss.dll",
        });

        Assert.Null(result);
    }

    [Fact]
    public void ANamedSystemWinsOverTheGenericWord()
    {
        // Both would match; the specific answer is the useful one.
        var result = AntiCheatMarkers.Detect(new[]
        {
            @"AntiCheat\readme.txt",
            @"BattlEye\BEClient_x64.dll",
        });

        Assert.Equal("BattlEye", result);
    }

    [Fact]
    public void TheGenericWordStillCountsWhenNothingNamedMatches()
    {
        Assert.Equal("Anti-cheat", AntiCheatMarkers.Detect(new[] { @"Binaries\Win64\anticheat_launcher.exe" }));
    }

    [Fact]
    public void AGameCalledVanguardIsNotFlagged()
    {
        // "Vanguard" is a Call of Duty title as well as Riot's anti-cheat, and Riot's does not live
        // in game folders anyway. Left out on purpose.
        Assert.Null(AntiCheatMarkers.Detect(new[] { @"Call of Duty Vanguard\vanguard.exe", @"vgk_notes.txt" }));
    }

    [Fact]
    public void NothingInGivesNothingOut()
    {
        Assert.Null(AntiCheatMarkers.Detect(System.Array.Empty<string>()));
        Assert.Null(AntiCheatMarkers.Detect(new[] { "", "   " }));
    }
}
