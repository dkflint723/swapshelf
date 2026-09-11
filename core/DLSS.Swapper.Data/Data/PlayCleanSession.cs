using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DLSS_Swapper.Data;

/// <summary>Where a play-clean session has got to.</summary>
internal enum PlayCleanPhase
{
    WaitingForStart,
    Running,
    Restoring,
}

/// <summary>How a play-clean session ended.</summary>
internal enum PlayCleanOutcome
{
    /// <summary>The game closed and the originals were put back.</summary>
    Restored,

    /// <summary>The game never appeared. Nothing was written.</summary>
    NeverStarted,

    /// <summary>The user stopped the watch. Nothing was written.</summary>
    Stopped,
}

/// <summary>
/// Watches a launched game and puts the saved originals back the moment it closes.
/// </summary>
/// <remarks>
/// <para>
/// For the swap you do not want to still be there next week: swapped files persist, which is the
/// whole reason the anti-cheat warning exists, and the failure mode is not the session you are
/// thinking about — it is wandering into a multiplayer lobby a fortnight later having forgotten
/// the dll is still modified. This makes the cleanup automatic for one session: play swapped,
/// and the shipped versions return on exit.
/// </para>
/// <para>
/// Every uncertain path fails towards today's behaviour. The app closing mid-watch, the game
/// never appearing, the watch being stopped — all leave the files exactly as they are, which is
/// the state every game is in without this feature. The one write is the same
/// <see cref="DllUpdateRunner.RevertGamesAsync"/> run the restore buttons use, so pins hold and
/// failures are reported the same way.
/// </para>
/// <para>
/// The game's processes are found by path: launching goes through a store (steam://) and hands
/// back no process, so anything executing from under the install folder is the game. Polled,
/// because that is the only shape that survives launchers, splash processes and games that are
/// three executables in a trench coat — the session ends when the count under that root has been
/// zero for two polls in a row.
/// </para>
/// </remarks>
internal sealed class PlayCleanSession
{
    static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(3);

    /// <summary>How long the game gets to appear. Store launchers can be slow to wake.</summary>
    static readonly TimeSpan _startTimeout = TimeSpan.FromMinutes(4);

    /// <summary>
    /// The one live session, or null. One at a time: a second watch would mean two games racing
    /// to be "the processes under a root", and nobody plays two games at once on purpose.
    /// </summary>
    public static PlayCleanSession? Current { get; private set; }

    /// <summary>Raised on completion however it ends, after <see cref="Current"/> is cleared.</summary>
    public static event Action<PlayCleanSession, PlayCleanOutcome, DllUpdateResult?>? SessionCompleted;

    public Game Game { get; }

    public PlayCleanPhase Phase { get; private set; } = PlayCleanPhase.WaitingForStart;

    /// <summary>Raised from the watcher thread. Marshal before touching UI.</summary>
    public event Action? PhaseChanged;

    readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

    PlayCleanSession(Game game)
    {
        Game = game;
    }

    /// <summary>
    /// Begins watching, or returns null when a session is already live or the game has no folder
    /// to watch.
    /// </summary>
    public static PlayCleanSession? Start(Game game)
    {
        if (Current is not null || string.IsNullOrWhiteSpace(game.InstallPath))
        {
            return null;
        }

        var session = new PlayCleanSession(game);
        Current = session;
        _ = Task.Run(session.RunAsync);
        return session;
    }

    /// <summary>Stops the watch and leaves every file as it is.</summary>
    public void Stop()
    {
        _cancellation.Cancel();
    }

    async Task RunAsync()
    {
        var outcome = PlayCleanOutcome.Stopped;
        DllUpdateResult? result = null;

        try
        {
            var deadline = DateTime.UtcNow + _startTimeout;
            var seenRunning = false;
            var quietPolls = 0;

            while (_cancellation.IsCancellationRequested == false)
            {
                var isRunning = AnyProcessUnder(Game.InstallPath);

                if (seenRunning == false)
                {
                    if (isRunning)
                    {
                        seenRunning = true;
                        SetPhase(PlayCleanPhase.Running);
                    }
                    else if (DateTime.UtcNow > deadline)
                    {
                        outcome = PlayCleanOutcome.NeverStarted;
                        return;
                    }
                }
                else if (isRunning)
                {
                    quietPolls = 0;
                }
                else
                {
                    // Two quiet polls, not one: plenty of games hand over from a splash or
                    // launcher process to the real one, and the gap between them must not read
                    // as the game having closed.
                    ++quietPolls;
                    if (quietPolls >= 2)
                    {
                        SetPhase(PlayCleanPhase.Restoring);
                        result = await DllUpdateRunner.RevertGamesAsync(new[] { Game }).ConfigureAwait(false);
                        outcome = PlayCleanOutcome.Restored;
                        return;
                    }
                }

                await Task.Delay(_pollInterval, _cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The user's stop. Files stay as they are, per the button that says so.
        }
        catch (Exception err)
        {
            Logger.Error(err, $"Play-clean watch for {Game.Title} failed.");
        }
        finally
        {
            Current = null;
            SessionCompleted?.Invoke(this, outcome, result);
        }
    }

    void SetPhase(PlayCleanPhase phase)
    {
        Phase = phase;
        PhaseChanged?.Invoke();
    }

    internal static bool AnyProcessUnder(string installPath)
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path is not null && IsPathUnder(path, installPath))
                {
                    return true;
                }
            }
            catch
            {
                // Protected and system processes refuse the module query. None of them are games.
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    /// <summary>Whether a file lives under a folder, by path alone.</summary>
    internal static bool IsPathUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalisedPath = path.Replace('/', Path.DirectorySeparatorChar);
        var normalisedRoot = root.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);

        return normalisedPath.StartsWith(normalisedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
