using System;
using System.Collections.Generic;
using System.Linq;
using DLSS_Swapper.Swapping;

namespace DLSS_Swapper.Tests;

/// <summary>
/// An in-memory journal that also remembers the order things were written in.
/// </summary>
internal sealed class FakeOperationJournal : IOperationJournal
{
    readonly Dictionary<string, OperationRecord> _records = new Dictionary<string, OperationRecord>();

    /// <summary>Every write, in order, as (id, state).</summary>
    public List<(string Id, OperationState State)> Writes { get; } = new List<(string, OperationState)>();

    /// <summary>Called on every write, with the record as written. For asserting on disk state at that moment.</summary>
    public Action<OperationRecord>? OnWrite { get; set; }

    /// <summary>When set, every write throws this.</summary>
    public Exception? FailWritesWith { get; set; }

    public int Count => _records.Count;

    public FakeOperationJournal With(OperationRecord record)
    {
        _records[record.Id] = record;
        return this;
    }

    public void Write(OperationRecord record)
    {
        if (FailWritesWith is not null)
        {
            throw FailWritesWith;
        }

        Writes.Add((record.Id, record.State));
        _records[record.Id] = record;
        OnWrite?.Invoke(record);
    }

    public void Remove(string id)
    {
        _records.Remove(id);
    }

    public IReadOnlyList<OperationRecord> ReadAll() => _records.Values.ToList();
}
