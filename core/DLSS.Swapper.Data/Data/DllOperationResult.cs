using DLSS_Swapper.Swapping;

namespace DLSS_Swapper.Data;

/// <summary>
/// What came of asking a game to swap or restore a dll.
/// </summary>
/// <remarks>
/// <para>
/// This was a three-tuple of success, message and "offer to relaunch as admin", and a caller could
/// tell the outcomes apart only by comparing message text. <see cref="Failure"/> carries the
/// executor's reason through, so a caller can tell a refusal that wants a decision - the file has
/// changed since the app last wrote it - from one that wants a fix.
/// </para>
/// <para>
/// The property names are the tuple's, so every existing caller reads it unchanged.
/// </para>
/// </remarks>
public readonly record struct DllOperationResult(bool Success, string Message, bool PromptToRelaunchAsAdmin, SwapFailure Failure = SwapFailure.None)
{
    /// <summary>
    /// True when the operation stopped to ask rather than because it could not be done. Nothing on
    /// disk changed; the caller repeats the call carrying the user's answer.
    /// </summary>
    public bool NeedsConfirmation => Failure == SwapFailure.TargetChanged;
}
