namespace SqliteVector.Net.Search;

using System;

/// <summary>
/// 검색 중 상위 K개 결과만 메모리에 유지하는 최소 힙(Min-Heap) 버퍼
/// </summary>
public class DenseTopKBuffer
{
    private readonly VectorSearchResult[] _heap;
    private int _count;
    public int Capacity { get; }

    public DenseTopKBuffer(int k)
    {
        Capacity = k;
        _heap = new VectorSearchResult[k];
    }

    public void Add(string id, float score, string? metadata)
    {
        if (_count < Capacity)
        {
            _heap[_count] = new VectorSearchResult(id, score, metadata);
            SiftUp(_count);
            _count++;
        }
        else if (score > _heap[0].Score)
        {
            _heap[0] = new VectorSearchResult(id, score, metadata);
            SiftDown(0);
        }
    }

    public VectorSearchResult[] GetSortedResults()
    {
        var result = new VectorSearchResult[_count];
        Array.Copy(_heap, result, _count);
        Array.Sort(result, (a, b) => b.Score.CompareTo(a.Score)); 
        return result;
    }

    private void SiftUp(int index)
    {
        var item = _heap[index];
        while (index > 0)
        {
            int parentIndex = (index - 1) / 2;
            var parent = _heap[parentIndex];
            if (item.Score >= parent.Score) break;
            _heap[index] = parent;
            index = parentIndex;
        }
        _heap[index] = item;
    }

    private void SiftDown(int index)
    {
        var item = _heap[index];
        while (index < _count / 2)
        {
            int leftChildIndex = 2 * index + 1;
            int rightChildIndex = leftChildIndex + 1;
            int minChildIndex = rightChildIndex < _count && _heap[rightChildIndex].Score < _heap[leftChildIndex].Score 
                ? rightChildIndex : leftChildIndex;

            if (item.Score <= _heap[minChildIndex].Score) break;
            _heap[index] = _heap[minChildIndex];
            index = minChildIndex;
        }
        _heap[index] = item;
    }
}
