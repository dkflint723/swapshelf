using System.Collections.Generic;
using DLSS_Swapper.Diagnostics;
using Xunit;

namespace DLSS_Swapper.Tests;

/// <summary>
/// Paths become placeholders; nothing else changes.
/// </summary>
public class RedactorTests
{
    static Redactor Rules(params (string Path, string Token)[] rules)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var (path, token) in rules)
        {
            pairs.Add(new KeyValuePair<string, string>(path, token));
        }
        return new Redactor(pairs);
    }

    [Fact]
    public void AnInstallPathBecomesItsTokenAndTheFileNameStays()
    {
        var redactor = Rules((@"D:\SteamLibrary\steamapps\common\Alan Wake 2", "<Game: Alan Wake 2>"));

        var result = redactor.Redact(@"Swapped D:\SteamLibrary\steamapps\common\Alan Wake 2\nvngx_dlss.dll to 310.2.1.0");

        Assert.Equal(@"Swapped <Game: Alan Wake 2>\nvngx_dlss.dll to 310.2.1.0", result);
    }

    [Fact]
    public void TheLongestMatchingPathWins()
    {
        // The game folder is inside the library root, which is inside the drive; each gets its own name.
        var redactor = Rules(
            (@"D:\SteamLibrary\steamapps\common", "<Steam library 1>"),
            (@"D:\SteamLibrary\steamapps\common\Alan Wake 2", "<Game: Alan Wake 2>"),
            (@"C:\Users\someone", "<UserProfile>"),
            (@"C:\Users\someone\AppData\Local\Swapshelf", "<Storage>"));

        var result = redactor.Redact(
            @"game=D:\SteamLibrary\steamapps\common\Alan Wake 2 root=D:\SteamLibrary\steamapps\common\ storage=C:\Users\someone\AppData\Local\Swapshelf\originals home=C:\Users\someone\Documents");

        Assert.Equal(@"game=<Game: Alan Wake 2> root=<Steam library 1>\ storage=<Storage>\originals home=<UserProfile>\Documents", result);
    }

    [Fact]
    public void CaseAndSeparatorDoNotMatter()
    {
        var redactor = Rules((@"D:\Games\Cyberpunk 2077", "<Game: Cyberpunk 2077>"));

        Assert.Equal("<Game: Cyberpunk 2077>/bin/x64", redactor.Redact("d:/games/CYBERPUNK 2077/bin/x64"));
        Assert.Equal(@"<Game: Cyberpunk 2077>\bin", redactor.Redact(@"D:\GAMES\cyberpunk 2077\bin"));
    }

    [Fact]
    public void HashesAndVersionsComeThroughUntouched()
    {
        var redactor = Rules((@"D:\Games\Some Game", "<Game: Some Game>"), (@"C:\Users\someone", "<UserProfile>"));
        var line = @"D:\Games\Some Game\nvngx_dlss.dll md5=2E3A0F8C2B1D4E5F6A7B8C9D0E1F2A3B sha256=E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855 v310.2.1.0";

        var result = redactor.Redact(line);

        Assert.Contains("md5=2E3A0F8C2B1D4E5F6A7B8C9D0E1F2A3B", result);
        Assert.Contains("sha256=E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", result);
        Assert.Contains("v310.2.1.0", result);
        Assert.StartsWith("<Game: Some Game>", result);
    }

    [Fact]
    public void TrailingSeparatorsAndEmptyRulesAreIgnored()
    {
        var redactor = Rules((@"D:\Games\Some Game\", "<Game: Some Game>"), ("", "<Nothing>"), ("   ", "<Blank>"));

        Assert.Equal(2, 2 - redactor.RuleCount + 1);   // one rule survived
        Assert.Equal(@"<Game: Some Game>\x.dll", redactor.Redact(@"D:\Games\Some Game\x.dll"));
        Assert.Equal("untouched text", redactor.Redact("untouched text"));
        Assert.Equal("", redactor.Redact(""));
    }

    [Fact]
    public void ADollarInATokenIsLiteral()
    {
        // Regex replacement strings treat $ specially; a token must come out as written.
        var redactor = Rules((@"D:\Games\X", "<$1 Game>"));

        Assert.Equal(@"<$1 Game>\a.dll", redactor.Redact(@"D:\Games\X\a.dll"));
    }
}
