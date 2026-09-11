using DLSS_Swapper.Compatibility;
using DLSS_Swapper.Data;
using DLSS_Swapper.Versioning;
using Xunit;

namespace DLSS_Swapper.Tests;

/// <summary>
/// How much reason there is to think a dll will work in a game, one rule at a time.
/// </summary>
/// <remarks>
/// The default matters most: with nothing known the answer is Unknown, not a cheerful guess. Every
/// other branch is a specific piece of evidence and a specific sentence.
/// </remarks>
public class ConfidenceRulesTests
{
    static ulong V(string version)
    {
        Assert.True(DllVersion.TryParse(version, out var rank));
        return rank;
    }

    static ConfidenceEvidence Dlss(string shipped, string candidate, DllFamily family = DllFamily.DlssSuperResolution)
    {
        return new ConfidenceEvidence()
        {
            Family = family,
            ShippedRank = V(shipped),
            CandidateRank = V(candidate),
        };
    }

    [Fact]
    public void NoEvidenceIsUnknown()
    {
        var result = ConfidenceRules.Assess(new ConfidenceEvidence() { Family = DllFamily.DlssSuperResolution });

        Assert.Equal(ConfidenceLevel.Unknown, result.Level);
        Assert.Equal(ConfidenceReason.NoEvidence, result.Reason);
    }

    [Fact]
    public void TheDefaultEvidenceIsUnknown()
    {
        // No family, no versions, nothing checked: the record struct's defaults alone.
        var result = ConfidenceRules.Assess(new ConfidenceEvidence());

        Assert.Equal(ConfidenceLevel.Unknown, result.Level);
    }

    [Fact]
    public void TheShippedFileIsKnownGoodWhateverElseIsTrue()
    {
        var result = ConfidenceRules.Assess(new ConfidenceEvidence()
        {
            CandidateIsShippedFile = true,
            CandidateIsDevBuild = true,
            CandidateSignatureTrusted = false,
        });

        Assert.Equal(ConfidenceLevel.KnownGood, result.Level);
        Assert.Equal(ConfidenceReason.ShippedFile, result.Reason);
    }

    [Fact]
    public void ADevBuildIsExperimentalEvenIfInstalled()
    {
        var result = ConfidenceRules.Assess(new ConfidenceEvidence() { CandidateIsDevBuild = true, CandidateIsInstalled = true });

        Assert.Equal(ConfidenceLevel.Experimental, result.Level);
        Assert.Equal(ConfidenceReason.DevBuild, result.Reason);
    }

    [Fact]
    public void AnUntrustedSignatureIsExperimental()
    {
        var result = ConfidenceRules.Assess(new ConfidenceEvidence() { CandidateSignatureTrusted = false, CandidateSwappedHereBefore = true });

        Assert.Equal(ConfidenceLevel.Experimental, result.Level);
        Assert.Equal(ConfidenceReason.UntrustedSignature, result.Reason);
    }

    [Fact]
    public void TheInstalledVersionIsLikely()
    {
        var result = ConfidenceRules.Assess(new ConfidenceEvidence() { CandidateIsInstalled = true });

        Assert.Equal(ConfidenceLevel.Likely, result.Level);
        Assert.Equal(ConfidenceReason.AlreadyInstalled, result.Reason);
    }

    [Fact]
    public void AVersionSwappedHereBeforeIsLikelyWithoutAnyVersionEvidence()
    {
        var result = ConfidenceRules.Assess(new ConfidenceEvidence() { Family = DllFamily.DlssFrameGeneration, GameUsesStreamline = true, CandidateSwappedHereBefore = true });

        Assert.Equal(ConfidenceLevel.Likely, result.Level);
        Assert.Equal(ConfidenceReason.SwappedHereBefore, result.Reason);
    }

    [Fact]
    public void AKindOfDllWithNoRuleIsUnknown()
    {
        var result = ConfidenceRules.Assess(new ConfidenceEvidence() { Family = DllFamily.Other, ShippedRank = V("1.0"), CandidateRank = V("2.0") });

        Assert.Equal(ConfidenceLevel.Unknown, result.Level);
        Assert.Equal(ConfidenceReason.UnknownFamily, result.Reason);
    }

    [Fact]
    public void TheSameLineIsLikely()
    {
        var result = ConfidenceRules.Assess(Dlss("3.5.0.0", "3.7.20.0"));

        Assert.Equal(ConfidenceLevel.Likely, result.Level);
        Assert.Equal(ConfidenceReason.SameLine, result.Reason);
    }

    [Fact]
    public void ANewerDlssLineIsLikely()
    {
        // DLSS 2 to 3 to 310: each replaces the last in a game that calls NGX directly.
        Assert.Equal(ConfidenceReason.NewerLine, ConfidenceRules.Assess(Dlss("2.5.1.0", "3.7.20.0")).Reason);
        Assert.Equal(ConfidenceReason.NewerLine, ConfidenceRules.Assess(Dlss("3.7.20.0", "310.2.1.0")).Reason);
        Assert.Equal(ConfidenceLevel.Likely, ConfidenceRules.Assess(Dlss("3.7.20.0", "310.2.1.0")).Level);
    }

    [Fact]
    public void AnOlderLineIsExperimental()
    {
        var result = ConfidenceRules.Assess(Dlss("3.7.20.0", "2.5.1.0"));

        Assert.Equal(ConfidenceLevel.Experimental, result.Level);
        Assert.Equal(ConfidenceReason.OlderLine, result.Reason);
    }

    [Fact]
    public void FrameGenerationAcrossLinesInAStreamlineGameIsExperimental()
    {
        var evidence = Dlss("3.7.10.0", "310.2.1.0", DllFamily.DlssFrameGeneration) with { GameUsesStreamline = true };

        var result = ConfidenceRules.Assess(evidence);

        Assert.Equal(ConfidenceLevel.Experimental, result.Level);
        Assert.Equal(ConfidenceReason.StreamlineFrameGeneration, result.Reason);
    }

    [Fact]
    public void FrameGenerationWithinTheLineInAStreamlineGameIsStillLikely()
    {
        var evidence = Dlss("3.7.0.0", "3.7.10.0", DllFamily.DlssFrameGeneration) with { GameUsesStreamline = true };

        Assert.Equal(ConfidenceReason.SameLine, ConfidenceRules.Assess(evidence).Reason);
    }

    [Fact]
    public void FrameGenerationAcrossLinesWithoutStreamlineIsLikely()
    {
        var evidence = Dlss("3.7.10.0", "310.2.1.0", DllFamily.DlssFrameGeneration);

        Assert.Equal(ConfidenceReason.NewerLine, ConfidenceRules.Assess(evidence).Reason);
    }

    [Fact]
    public void SuperResolutionInAStreamlineGameIsNotHeldBack()
    {
        var evidence = Dlss("3.7.10.0", "310.2.1.0") with { GameUsesStreamline = true };

        Assert.Equal(ConfidenceReason.NewerLine, ConfidenceRules.Assess(evidence).Reason);
    }

    [Fact]
    public void FsrIsDropInWithinItsMinorLineOnly()
    {
        Assert.Equal(ConfidenceReason.SameLine, ConfidenceRules.Assess(Dlss("3.1.1", "3.1.4", DllFamily.Fsr)).Reason);

        var acrossLines = ConfidenceRules.Assess(Dlss("3.1.4", "4.0.0", DllFamily.Fsr));
        Assert.Equal(ConfidenceLevel.Experimental, acrossLines.Level);
        Assert.Equal(ConfidenceReason.DifferentLine, acrossLines.Reason);
    }

    [Fact]
    public void XessTwoRunsInXessOneGames()
    {
        var result = ConfidenceRules.Assess(Dlss("1.3.0.0", "2.0.1.0", DllFamily.Xess));

        Assert.Equal(ConfidenceLevel.Likely, result.Level);
        Assert.Equal(ConfidenceReason.NewerLine, result.Reason);
    }

    [Theory]
    [InlineData(GameAssetType.DLSS, DllFamily.DlssSuperResolution)]
    [InlineData(GameAssetType.DLSS_G, DllFamily.DlssFrameGeneration)]
    [InlineData(GameAssetType.DLSS_D, DllFamily.DlssRayReconstruction)]
    [InlineData(GameAssetType.FSR_31_DX12, DllFamily.Fsr)]
    [InlineData(GameAssetType.FSR_31_VK, DllFamily.Fsr)]
    [InlineData(GameAssetType.XeSS, DllFamily.Xess)]
    [InlineData(GameAssetType.XeSS_FG, DllFamily.Xess)]
    [InlineData(GameAssetType.XeLL, DllFamily.Xess)]
    [InlineData(GameAssetType.DLSS_NR, DllFamily.Other)]
    [InlineData(GameAssetType.DLSS_BACKUP, DllFamily.Unknown)]
    [InlineData(GameAssetType.Unknown, DllFamily.Unknown)]
    public void EverySwappableTypeHasAFamily(GameAssetType assetType, DllFamily expected)
    {
        Assert.Equal(expected, ConfidenceRules.FamilyOf(assetType));
    }
}
