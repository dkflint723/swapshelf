using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DLSS_Swapper.Data;

/// <summary>
/// The SHA-256 of each manifest dll this machine has seen, keyed by the MD5 the manifest knows it by.
/// </summary>
/// <remarks>
/// <para>
/// The manifest carries MD5 only, and is reloaded on every launch, so a SHA-256 learned from a
/// downloaded or swapped file would be gone by the next session. This keeps it: a small JSON file
/// beside the other dynamic ones, read once, written whenever something new is learned, and put
/// back onto the records after each manifest load.
/// </para>
/// <para>
/// An entry is only ever learned from a file whose MD5 already matched the record. It is the second
/// lock on the same door, not a substitute for the first.
/// </para>
/// </remarks>
internal static class LocalDigestStore
{
    const string FileName = "sha256.json";

    static readonly object _lock = new object();
    static Dictionary<string, string>? _digests;
    static string? _loadedFrom;

    static string PathFor() => Path.Combine(Storage.GetDynamicJsonFolder(), FileName);

    internal static string? TryGet(string md5)
    {
        if (string.IsNullOrWhiteSpace(md5))
        {
            return null;
        }

        lock (_lock)
        {
            return Digests().TryGetValue(md5.Trim().ToUpperInvariant(), out var sha256) ? sha256 : null;
        }
    }

    internal static void Remember(string md5, string sha256)
    {
        if (string.IsNullOrWhiteSpace(md5) || string.IsNullOrWhiteSpace(sha256))
        {
            return;
        }

        lock (_lock)
        {
            var digests = Digests();
            var key = md5.Trim().ToUpperInvariant();
            var value = sha256.Trim().ToUpperInvariant();
            if (digests.TryGetValue(key, out var existing) && existing == value)
            {
                return;
            }

            digests[key] = value;

            try
            {
                var path = PathFor();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(digests, SourceGenerationContext.Default.DictionaryStringString));
                File.Move(temporary, path, true);
            }
            catch (Exception err)
            {
                // Remembered for this session regardless; only the durable copy failed.
                Logger.Warning($"Could not save the SHA-256 store: {err.Message}");
            }
        }
    }

    /// <summary>Puts remembered SHA-256s onto records that have none. Records that carry their own keep it.</summary>
    internal static void Apply(IEnumerable<DLLRecord>? records)
    {
        if (records is null)
        {
            return;
        }

        foreach (var record in records)
        {
            if (string.IsNullOrEmpty(record.Sha256Hash) == false)
            {
                continue;
            }

            var remembered = TryGet(record.MD5Hash);
            if (remembered is not null)
            {
                record.Sha256Hash = remembered;
            }
        }
    }

    /// <summary>Forgets the loaded copy, so the next read comes from disk. For tests that move the storage folder.</summary>
    internal static void Reload()
    {
        lock (_lock)
        {
            _digests = null;
            _loadedFrom = null;
        }
    }

    static Dictionary<string, string> Digests()
    {
        var path = PathFor();
        if (_digests is not null && string.Equals(_loadedFrom, path, StringComparison.OrdinalIgnoreCase))
        {
            return _digests;
        }

        var loaded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(path))
            {
                var read = JsonSerializer.Deserialize(File.ReadAllText(path), SourceGenerationContext.Default.DictionaryStringString);
                if (read is not null)
                {
                    foreach (var (key, value) in read)
                    {
                        loaded[key] = value;
                    }
                }
            }
        }
        catch (Exception err)
        {
            Logger.Warning($"Could not read the SHA-256 store; starting it again: {err.Message}");
        }

        _digests = loaded;
        _loadedFrom = path;
        return loaded;
    }
}
