namespace SqliteVector.Net.Runtime;

using System;
using System.Threading;
using SqliteVector.Net.Storage;
using SqliteVector.Net.Search;

/// <summary>
/// G8: Search Snapshot Lifecycle
/// </summary>
public sealed class SearchSnapshot : IDisposable
{
    public long Generation { get; }
    
    // C4: 스냅샷이 여러 세그먼트 엔진들을 소유함
    public System.Collections.Generic.IReadOnlyList<MemoryMappedSearchEngine> Engines { get; } 
    public System.Collections.Generic.IReadOnlyDictionary<long, System.Collections.BitArray> LiveSets { get; }
    
    private int _refCount = 1;

    public SearchSnapshot(long generation, System.Collections.Generic.IReadOnlyList<MemoryMappedSearchEngine> engines, System.Collections.Generic.IReadOnlyDictionary<long, System.Collections.BitArray> liveSets)
    {
        Generation = generation;
        Engines = engines;
        LiveSets = liveSets;
    }

    public bool TryAddReference()
    {
        while (true)
        {
            int current = _refCount;
            if (current == 0) return false; // Already disposed
            
            if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                return true;
        }
    }

    public void Retire()
    {
        Dispose(); // Decrements the initial "Active" reference
    }

    public void Dispose()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            // 참조가 0이 되면 모든 소유한 엔진의 참조 카운트를 감소
            foreach (var engine in Engines)
            {
                engine.Release();
            }
        }
    }
}
