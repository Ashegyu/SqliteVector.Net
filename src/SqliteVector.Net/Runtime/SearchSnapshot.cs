namespace SqliteVector.Net.Runtime;

using System;
using System.Threading;
using SqliteVector.Net.Storage;

/// <summary>
/// G8: Search Snapshot
/// 34, 35항: 검색을 시작할 때 현재 활성화된 세그먼트의 '특정 시점'을 고정합니다.
/// 검색이 진행되는 동안 백그라운드에서 Writer가 새 벡터를 추가하거나 Compaction이 일어나도 
/// 검색 결과의 일관성(Isolation)이 100% 보장됩니다.
/// </summary>
public sealed class SearchSnapshot : IDisposable
{
    public long Generation { get; }
    
    // G8: 단일 세그먼트 가정. 추후 SealedSegment[] 배열로 확장 가능
    public SegmentReader Reader { get; } 
    
    private int _refCount = 1;

    public SearchSnapshot(long generation, SegmentReader reader)
    {
        Generation = generation;
        Reader = reader;
    }

    public SearchSnapshot AddReference()
    {
        Interlocked.Increment(ref _refCount);
        return this;
    }

    public void Dispose()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            // 참조 카운트가 0이 되면 안전하게 리소스 해제
            // 70항: 검색 중에는 Dispose가 Mmap을 강제 해제하지 못하도록 보호됨
            Reader.Dispose();
        }
    }
}
