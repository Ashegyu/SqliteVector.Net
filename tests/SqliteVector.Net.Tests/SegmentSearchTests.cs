namespace SqliteVector.Net.Tests;

using System;
using System.IO;
using FluentAssertions;
using SqliteVector.Net.Catalog;
using SqliteVector.Net.Search;
using SqliteVector.Net.Storage;
using Xunit;

public class SegmentSearchTests
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
    public void G4_G5_WriteReadAndSIMDSearch_ShouldMatchOracle()
    {
        string testFile = Path.Combine(Path.GetTempPath(), $"search_test_{Guid.NewGuid()}.vec");
        try
        {
            int dimensions = 1536;
            int capacity = 50;
            
            // 1. Writer 생성 및 데이터 주입
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

            // 2. 검색 쿼리 준비
            var query = GenerateRandomVector(999, dimensions);
            var buffer = new DenseTopKBuffer(5);

            // 3. Reader & Engine 구동 (G4 & G5)
            using (var reader = new SegmentReader(testFile))
            {
                var engine = new ExactSearchEngine(reader, VectorMetric.Cosine);
                engine.Search(query, 5, buffer);
            }

            // 4. 결과 검증 (오라클과 비교)
            var results = buffer.GetSortedResults();
            results.Should().HaveCount(5);

            // 오라클(Ground truth) 계산
            var oracleResults = new (int Id, float Score)[capacity];
            for (int i = 0; i < capacity; i++)
            {
                oracleResults[i] = (i, ScalarOracle.CosineSimilarity(query, vectors[i]));
            }
            
            // 오름차순 정렬 후 Top 5
            Array.Sort(oracleResults, (a, b) => b.Score.CompareTo(a.Score));

            for (int i = 0; i < 5; i++)
            {
                // Engine 결과의 ID는 현재 RecordIndex.ToString()으로 임시 처리되어 있음
                results[i].Id.Should().Be(oracleResults[i].Id.ToString());
                results[i].Score.Should().BeApproximately(oracleResults[i].Score, 0.0001f);
            }
        }
        finally
        {
            if (File.Exists(testFile)) File.Delete(testFile);
        }
    }
}
