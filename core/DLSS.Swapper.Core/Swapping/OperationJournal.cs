using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DLSS_Swapper.Swapping;

public enum OperationKind
{
    Swap,
    Reset,
}

public enum OperationState
{
    /// <summary>Backups and staged copies are being written beside the targets. Nothing the game loads has changed.</summary>
    Staging,

    /// <summary>Targets are being renamed over. Some may already hold the new file, with the old one beside them.</summary>
    Committing,
}

/// <summary>Who an operation is for, so a notice about it can name the game.</summary>
public readonly record struct OperationLabel(string? GameId, string? GameTitle);

/// <summary>
/// One in-flight swap or reset, as written to disk before the game folder is touched.
/// </summary>
/// <remarks>
/// Everything recovery needs and nothing else: which paths were being written, which backups this
/// operation created (so a rollback can remove them, as the in-process rollback does), and how far
/// it had got. The staged and previous copies are derived from the target paths by suffix, the way
/// the executor derives them.
/// </remarks>
public sealed class OperationRecord
{
    public string Id { get; set; } = string.Empty;
    public OperationKind Kind { get; set; }
    public OperationState State { get; set; }
    public string? GameId { get; set; }
    public string? GameTitle { get; set; }
    public string? SourcePath { get; set; }
    public List<string> TargetPaths { get; set; } = new List<string>();
    public List<string> CreatedBackupPaths { get; set; } = new List<string>();
    public DateTime StartedAtUtc { get; set; }

    /// <summary>
    /// The process that wrote this record, so recovery can tell an operation still in progress from
    /// one that was cut off. Zero on a record written before owners were kept: treated as ended.
    /// </summary>
    public int ProcessId { get; set; }

    /// <summary>When that process started, so a process id Windows has since handed to something else is not mistaken for it.</summary>
    public DateTime? ProcessStartedAtUtc { get; set; }
}

/// <summary>
/// Whether the process that wrote a journal record is still running.
/// </summary>
/// <remarks>
/// <para>
/// Two processes write the journal: the app, and the command line a Steam plugin starts. Each runs
/// recovery when it starts, and a record whose owner is still alive is not an interrupted operation
/// but one in progress - rolling it back would pull files out from under a swap that is part way
/// through its renames. So a record names its process, and its start time, because Windows reuses
/// process ids and a reused id belongs to some other program.
/// </para>
/// <para>
/// When the answer cannot be had - a process this one is not allowed to inspect - it is taken to be
/// running. Leaving a record for the next launch costs nothing; rolling back a live operation is the
/// harm the check exists to prevent.
/// </para>
/// </remarks>
public static class OperationOwner
{
    static readonly Lazy<(int ProcessId, DateTime? StartedAtUtc)> _current = new Lazy<(int, DateTime?)>(ReadCurrent);

    /// <summary>This process, as a record written now would name it.</summary>
    public static (int ProcessId, DateTime? StartedAtUtc) Current => _current.Value;

    static (int, DateTime?) ReadCurrent()
    {
        using (var process = Process.GetCurrentProcess())
        {
            DateTime? started = null;
            try
            {
                started = process.StartTime.ToUniversalTime();
            }
            catch (Exception)
            {
                // Recorded without a start time; a reader then trusts the id alone.
            }

            return (process.Id, started);
        }
    }

    public static bool IsRunning(OperationRecord record)
    {
        if (record.ProcessId <= 0)
        {
            return false;
        }

        try
        {
            using (var process = Process.GetProcessById(record.ProcessId))
            {
                if (process.HasExited)
                {
                    return false;
                }

                if (record.ProcessStartedAtUtc is null)
                {
                    return true;
                }

                var started = process.StartTime.ToUniversalTime();
                return Math.Abs((started - record.ProcessStartedAtUtc.Value).TotalSeconds) < 1;
            }
        }
        catch (ArgumentException)
        {
            // No process has that id now.
            return false;
        }
        catch (InvalidOperationException)
        {
            // It exited while being looked at.
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }
}

/// <summary>
/// Where in-flight operations are recorded. One record per operation, removed when it is done.
/// </summary>
public interface IOperationJournal
{
    /// <summary>Creates or replaces the record with this id.</summary>
    void Write(OperationRecord record);

    void Remove(string id);

    /// <summary>Every record still present: the operations that did not finish.</summary>
    IReadOnlyList<OperationRecord> ReadAll();
}

/// <summary>No journal. What the executor uses when nobody gives it one.</summary>
public sealed class NullOperationJournal : IOperationJournal
{
    public static NullOperationJournal Instance { get; } = new NullOperationJournal();

    public void Write(OperationRecord record)
    {
    }

    public void Remove(string id)
    {
    }

    public IReadOnlyList<OperationRecord> ReadAll() => Array.Empty<OperationRecord>();
}

/// <summary>
/// One JSON file per operation in a folder of the app's own.
/// </summary>
/// <remarks>
/// <para>
/// Written to a temporary name and moved into place, so a session that ends while the journal
/// itself is being written leaves either the previous record or the new one, never half of one.
/// </para>
/// <para>
/// A file that will not parse is skipped rather than deleted: it is somebody's evidence of what
/// went wrong, and reading it is a person's job.
/// </para>
/// </remarks>
public sealed class FileOperationJournal : IOperationJournal
{
    readonly string _folder;

    public FileOperationJournal(string folder)
    {
        _folder = folder;
    }

    public string Folder => _folder;

    public void Write(OperationRecord record)
    {
        Directory.CreateDirectory(_folder);

        var path = PathFor(record.Id);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(record, OperationJournalJsonContext.Default.OperationRecord));
        File.Move(temporaryPath, path, true);
    }

    public void Remove(string id)
    {
        var path = PathFor(id);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public IReadOnlyList<OperationRecord> ReadAll()
    {
        if (Directory.Exists(_folder) == false)
        {
            return Array.Empty<OperationRecord>();
        }

        var records = new List<OperationRecord>();
        foreach (var path in Directory.EnumerateFiles(_folder, "*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize(File.ReadAllText(path), OperationJournalJsonContext.Default.OperationRecord);
                if (record is not null && string.IsNullOrWhiteSpace(record.Id) == false)
                {
                    records.Add(record);
                }
            }
            catch (Exception)
            {
                // Left in place, see the class remarks.
            }
        }

        records.Sort((a, b) => a.StartedAtUtc.CompareTo(b.StartedAtUtc));
        return records;
    }

    string PathFor(string id) => Path.Combine(_folder, id + ".json");
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(OperationRecord))]
internal partial class OperationJournalJsonContext : JsonSerializerContext
{
}
