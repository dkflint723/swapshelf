using System;
using DLSS_Swapper.Swapping;

namespace DLSS_Swapper.Data;

/// <summary>
/// The executor the app swaps with: the real filesystem and the on-disk operation journal.
/// </summary>
/// <remarks>
/// One place, so every write path journals the same way and the startup recovery reads the same
/// journal they wrote.
/// </remarks>
internal static class SwapExecutors
{
    internal static IOperationJournal Journal { get; } = new FileOperationJournal(Storage.GetOperationsFolder());

    internal static DllSwapExecutor Create()
    {
        return new DllSwapExecutor(PhysicalFileSystem.Instance, Journal);
    }

    /// <summary>
    /// Puts back whatever an operation the previous session did not finish had changed, and says
    /// what it did. Null when there was nothing to do.
    /// </summary>
    internal static InterruptedSwapNotice? RecoverInterrupted()
    {
        var report = SwapRecovery.Recover(PhysicalFileSystem.Instance, Journal);

        foreach (var operation in report.Operations)
        {
            var who = string.IsNullOrEmpty(operation.Record.GameTitle) ? operation.Record.GameId ?? "an unknown game" : operation.Record.GameTitle;
            Logger.Warning($"A {operation.Record.Kind} for {who} started {operation.Record.StartedAtUtc:u} was not finished by the previous session; {operation.RestoredPaths.Count} of {operation.Record.TargetPaths.Count} target(s) had been renamed and were put back.");

            foreach (var warning in operation.Warnings)
            {
                Logger.Warning(warning);
            }
        }

        return InterruptedSwapNotice.For(report);
    }
}
