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
    
    // G8: 읽기 전용 세그먼트 스냅샷
    public MemoryMappedSearchEngine MMapEngine { get; } 
    public System.Collections.BitArray LiveSet { get; }
    
    private int _refCount = 1;

    public SearchSnapshot(long generation, MemoryMappedSearchEngine engine, System.Collections.BitArray liveSet)
    {
        Generation = generation;
        MMapEngine = engine;
        LiveSet = liveSet;
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
            // 모든 참조가 해제되면 MMap 자원 해제
            MMapEngine.Dispose();
        }
    }
}
