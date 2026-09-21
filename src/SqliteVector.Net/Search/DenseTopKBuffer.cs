namespace SqliteVector.Net.Search;

using System;

// O(K) Allocation을 위한 값 타입 후보 구조체
public readonly record struct Candidate(long SegmentId, int RecordIndex, float Score);

public class DenseTopKBuffer
{
    private readonly Candidate[] _heap;
    private int _count;

    public int Capacity { get; }

    public DenseTopKBuffer(int k)
    {
        if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k), "TopK must be greater than 0");
        Capacity = k;
        _heap = new Candidate[k];
    }

    public void Add(long segmentId, int recordIndex, float score)
    {
        if (_count < Capacity)
        {
            _heap[_count] = new Candidate(segmentId, recordIndex, score);
            _count++;
            if (_count == Capacity) BuildMinHeap();
        }
        else if (score > _heap[0].Score)
        {
            _heap[0] = new Candidate(segmentId, recordIndex, score);
            Heapify(0);
        }
    }

    public Candidate[] GetSortedResults()
    {
        var results = new Candidate[_count];
        Array.Copy(_heap, results, _count);
        Array.Sort(results, (a, b) => b.Score.CompareTo(a.Score));
        return results;
    }

    private void BuildMinHeap()
    {
        for (int i = (_count / 2) - 1; i >= 0; i--)
            Heapify(i);
    }

    private void Heapify(int i)
    {
        int smallest = i;
        int left = 2 * i + 1;
        int right = 2 * i + 2;

        if (left < _count && _heap[left].Score < _heap[smallest].Score) smallest = left;
        if (right < _count && _heap[right].Score < _heap[smallest].Score) smallest = right;

        if (smallest != i)
        {
            var temp = _heap[i];
            _heap[i] = _heap[smallest];
            _heap[smallest] = temp;
            Heapify(smallest);
        }
    }
}
