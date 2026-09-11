using DLSS_Swapper.Compatibility;
using DLSS_Swapper.Helpers;

namespace DLSS_Swapper.Data;

/// <summary>
/// A confidence as the user sees it: a shape, a short label and one sentence of reason.
/// </summary>
/// <remarks>
/// Four glyphs of four different shapes - a check, an information mark, a warning triangle, a
/// question mark - so the level reads without colour. The label says the level in words and the
/// reason says which rule decided it; a screen reader gets both.
/// </remarks>
public sealed class ConfidenceDisplay
{
    public required ConfidenceLevel Level { get; init; }
    public required string Glyph { get; init; }
    public required string Label { get; init; }
    public required string Reason { get; init; }

    public string Accessible => $"{Label}. {Reason}";

    public static ConfidenceDisplay For(Confidence confidence)
    {
        return new ConfidenceDisplay()
        {
            Level = confidence.Level,
            Glyph = GlyphFor(confidence.Level),
            Label = ResourceHelper.GetString("Confidence_" + confidence.Level),
            Reason = ResourceHelper.GetString("Confidence_Reason_" + confidence.Reason),
        };
    }

    static string GlyphFor(ConfidenceLevel level)
    {
        switch (level)
        {
            case ConfidenceLevel.KnownGood:
                return "\uE73E";   // CheckMark
            case ConfidenceLevel.Likely:
                return "\uE946";   // Info
            case ConfidenceLevel.Experimental:
                return "\uE7BA";   // Warning
            default:
                return "\uE897";   // Help
        }
    }
}
