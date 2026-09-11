using System;
using System.Collections.Generic;
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
