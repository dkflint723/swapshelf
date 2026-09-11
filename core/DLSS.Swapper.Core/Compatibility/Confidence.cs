using DLSS_Swapper.Data;

namespace DLSS_Swapper.Compatibility;

/// <summary>
/// How much reason there is to think a dll will work in a particular game.
/// </summary>
/// <remarks>
/// Ordered so that a higher value is more confidence, which is all the ordering means. Text and
/// a shape carry it to the user; a colour never carries it alone.
/// </remarks>
public enum ConfidenceLevel
{
    /// <summary>Nothing is known either way. The default, and the honest answer for most swaps.</summary>
    Unknown = 0,

    /// <summary>A specific reason to expect trouble.</summary>
    Experimental = 1,

    /// <summary>The kind of swap that works as a rule, with nothing known against it.</summary>
    Likely = 2,

    /// <summary>The file the game shipped with.</summary>
    KnownGood = 3,
}

/// <summary>
/// Which rule decided the level. Each is one sentence in the app.
/// </summary>
public enum ConfidenceReason
{
    NoEvidence,
    ShippedFile,
    AlreadyInstalled,
    DevBuild,
    UntrustedSignature,
    SwappedHereBefore,
    StreamlineFrameGeneration,
    SameLine,
    NewerLine,
    OlderLine,
    DifferentLine,
    UnknownFamily,
}

/// <summary>
/// The kinds of dll that share compatibility rules.
/// </summary>
public enum DllFamily
{
    Unknown,
    DlssSuperResolution,
    DlssFrameGeneration,
    DlssRayReconstruction,
    Fsr,
    Xess,
    Other,
}

/// <summary>
/// Everything the rules look at, gathered by whoever has the game and the candidate in hand.
/// </summary>
/// <remarks>
/// Plain values rather than the app's game and record types, so the rules live in the core library
/// and every branch is a test with no database behind it. Ranks are the packed versions
/// <c>DllVersionRanking</c> produces, so they compare the way the rest of the app compares.
/// </remarks>
public sealed record ConfidenceEvidence
{
    public DllFamily Family { get; init; } = DllFamily.Unknown;

    /// <summary>The version the game shipped with, when known.</summary>
    public ulong? ShippedRank { get; init; }

    /// <summary>The version being considered, when it parses.</summary>
    public ulong? CandidateRank { get; init; }

    /// <summary>The candidate hashes to the file the game shipped with.</summary>
    public bool CandidateIsShippedFile { get; init; }

    /// <summary>The candidate is the file installed right now.</summary>
    public bool CandidateIsInstalled { get; init; }

    /// <summary>This game's history shows this version swapped in before.</summary>
    public bool CandidateSwappedHereBefore { get; init; }

    public bool CandidateIsDevBuild { get; init; }

    /// <summary>Signed by the vendor that makes this kind of dll. True when nobody has checked.</summary>
    public bool CandidateSignatureTrusted { get; init; } = true;

    /// <summary>The game loads NVIDIA's dlls through Streamline rather than calling NGX itself.</summary>
    public bool GameUsesStreamline { get; init; }
}

public readonly record struct Confidence(ConfidenceLevel Level, ConfidenceReason Reason);

/// <summary>
/// The rules, in the order they are asked. The first that applies answers.
/// </summary>
/// <remarks>
/// <para>
/// Advisory only. Nothing here stops a swap; the hash, signature, architecture and running-game
/// checks do that, each with its own sentence. This tells the user, before they press the button,
/// how much is known about the version they picked for the game they picked it for.
/// </para>
/// <para>
/// The version rules come from how the SDKs are shipped. NVIDIA's nvngx dlls are built to be
/// drop-in across release lines going forward, and DLSS 2, 3 and 310 all replace one another in
/// games that call NGX directly; a game that loads them through Streamline gets frame generation
/// and ray reconstruction as a matched set of plugins, which is where cross-line swaps of those
/// two are known to go wrong. Intel's XeSS 2 dlls run in XeSS 1 games by design. AMD's FSR dlls
/// are drop-in within a line - 3.1.x for 3.1.y - and not across one.
/// </para>
/// </remarks>
public static class ConfidenceRules
{
    public static Confidence Assess(ConfidenceEvidence evidence)
    {
        if (evidence.CandidateIsShippedFile)
        {
            return new Confidence(ConfidenceLevel.KnownGood, ConfidenceReason.ShippedFile);
        }

        if (evidence.CandidateIsDevBuild)
        {
            return new Confidence(ConfidenceLevel.Experimental, ConfidenceReason.DevBuild);
        }

        if (evidence.CandidateSignatureTrusted == false)
        {
            return new Confidence(ConfidenceLevel.Experimental, ConfidenceReason.UntrustedSignature);
        }

        if (evidence.CandidateIsInstalled)
        {
            return new Confidence(ConfidenceLevel.Likely, ConfidenceReason.AlreadyInstalled);
        }

        if (evidence.CandidateSwappedHereBefore)
        {
            return new Confidence(ConfidenceLevel.Likely, ConfidenceReason.SwappedHereBefore);
        }

        if (evidence.Family == DllFamily.Unknown || evidence.Family == DllFamily.Other)
        {
            return new Confidence(ConfidenceLevel.Unknown, ConfidenceReason.UnknownFamily);
        }

        if (evidence.ShippedRank is null || evidence.CandidateRank is null)
        {
            return new Confidence(ConfidenceLevel.Unknown, ConfidenceReason.NoEvidence);
        }

        var shipped = evidence.ShippedRank.Value;
        var candidate = evidence.CandidateRank.Value;
        var sameLine = SameLine(evidence.Family, shipped, candidate);

        var isMatchedSetInStreamline = evidence.GameUsesStreamline
            && (evidence.Family == DllFamily.DlssFrameGeneration || evidence.Family == DllFamily.DlssRayReconstruction);
        if (isMatchedSetInStreamline && sameLine == false)
        {
            return new Confidence(ConfidenceLevel.Experimental, ConfidenceReason.StreamlineFrameGeneration);
        }

        if (sameLine)
        {
            return new Confidence(ConfidenceLevel.Likely, ConfidenceReason.SameLine);
        }

        if (candidate > shipped)
        {
            return evidence.Family == DllFamily.Fsr
                ? new Confidence(ConfidenceLevel.Experimental, ConfidenceReason.DifferentLine)
                : new Confidence(ConfidenceLevel.Likely, ConfidenceReason.NewerLine);
        }

        return new Confidence(ConfidenceLevel.Experimental, ConfidenceReason.OlderLine);
    }

    /// <summary>The family a swappable asset type belongs to. Backup types are not swapped and are Unknown.</summary>
    public static DllFamily FamilyOf(GameAssetType assetType)
    {
        switch (assetType)
        {
            case GameAssetType.DLSS:
                return DllFamily.DlssSuperResolution;
            case GameAssetType.DLSS_G:
                return DllFamily.DlssFrameGeneration;
            case GameAssetType.DLSS_D:
                return DllFamily.DlssRayReconstruction;
            case GameAssetType.FSR_31_DX12:
            case GameAssetType.FSR_31_VK:
                return DllFamily.Fsr;
            case GameAssetType.XeSS:
            case GameAssetType.XeSS_DX11:
            case GameAssetType.XeSS_FG:
            case GameAssetType.XeLL:
                return DllFamily.Xess;
            case GameAssetType.DLSS_NR:
                return DllFamily.Other;
            default:
                return DllFamily.Unknown;
        }
    }

    /// <summary>
    /// Whether two versions are in the same release line: the same major, or for FSR the same
    /// major and minor, since 3.1 is the line and 3.0 was not a drop-in file at all.
    /// </summary>
    static bool SameLine(DllFamily family, ulong a, ulong b)
    {
        var majorA = (ushort)(a >> 48);
        var majorB = (ushort)(b >> 48);
        if (majorA != majorB)
        {
            return false;
        }

        if (family != DllFamily.Fsr)
        {
            return true;
        }

        return (ushort)(a >> 32) == (ushort)(b >> 32);
    }
}
