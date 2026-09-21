namespace SqliteVector.Net.Tests;

using System;
using System.IO;
using FluentAssertions;
using SqliteVector.Net.Catalog;
using SqliteVector.Net.Search;
using SqliteVector.Net.Storage;
using Xunit;

public class MMapSearchEngineTests
{
    private float[] GenerateRandomVector(int seed, int dimensions)
    {
        var random = new Random(seed);
        var vec = new float[dimensions];
        for (int i = 0; i < dimensions; i++)
            vec[i] = (float)(random.NextDouble() * 2 - 1);
        return vec;
    }

    [Fact]
    public void G9_MMapSearch_ShouldMatchExactStreamingResults_WithZeroCopy()
    {
        string testFile = Path.Combine(Path.GetTempPath(), $"mmap_{Guid.NewGuid()}.vec");
        try
        {
            int dimensions = 768; // 3072 bytes Payload
            int capacity = 100;
            
            var header = new SegmentHeader(1, 1, dimensions, VectorElementType.Float32, capacity, 64);
            
            var vectors = new float[capacity][];
            using (var writer = new ActiveSegmentWriter(testFile, header))
            {
                for (int i = 0; i < capacity; i++)
                {
                    vectors[i] = GenerateRandomVector(i, dimensions);
                    writer.AppendVector(vectors[i], generation: 1);
                }
            }

            var query = GenerateRandomVector(42, dimensions);
            var buffer = new DenseTopKBuffer(3);

            // G9 MMap Engine 구동
            using (var mmapEngine = new MemoryMappedSearchEngine(testFile, VectorMetric.DotProduct))
            {
                mmapEngine.Search(query, buffer);
            }

            var results = buffer.GetSortedResults();
            results.Should().HaveCount(3);

            // Oracle 검증
            var oracleResults = new (int Id, float Score)[capacity];
            for (int i = 0; i < capacity; i++)
                oracleResults[i] = (i, ScalarOracle.DotProduct(query, vectors[i]));
            
            Array.Sort(oracleResults, (a, b) => b.Score.CompareTo(a.Score));

            for (int i = 0; i < 3; i++)
            {
                results[i].RecordIndex.Should().Be(oracleResults[i].Id);
                results[i].Score.Should().BeApproximately(oracleResults[i].Score, 0.0001f);
            }
        }
        finally
        {
            try { if (File.Exists(testFile)) File.Delete(testFile); }
            catch { /* Ignore MMap delayed release locks in test */ }
        }
    }
}
