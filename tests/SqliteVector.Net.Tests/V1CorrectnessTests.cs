namespace SqliteVector.Net.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using SqliteVector.Net;
using Xunit;

public class V1CorrectnessTests
{
    private readonly int _dimensions = 1536;

    private float[] GenerateRandomVector(int seed, int dimensions)
    {
        var random = new Random(seed);
        var vec = new float[dimensions];
        for (int i = 0; i < dimensions; i++)
        {
            // 정규화를 안한 임의의 벡터
            vec[i] = (float)(random.NextDouble() * 2 - 1);
        }
        return vec;
    }

    [Fact]
    public async Task G0_Search_ShouldMatch_ScalarOracleDotProduct()
    {
        // Arrange
        string dbPath = $"file::memory:?cache=shared";
        using var store = new SqliteVectorStore(dbPath, _dimensions);

        var vectors = new Dictionary<string, float[]>();
        for (int i = 0; i < 100; i++)
        {
            string id = $"doc-{i}";
            var vec = GenerateRandomVector(i, _dimensions);
            vectors[id] = vec;
            await store.UpsertAsync(id, vec, $"metadata-{i}");
        }

        float[] query = GenerateRandomVector(999, _dimensions);

        // Act (V1 SIMD Implementation)
        var results = await store.SearchAsync(query, topK: 5);

        // Assert (Compare with Scalar Oracle)
        results.Should().HaveCount(5);

        // 오라클(Ground truth) 결과 계산
        var oracleResults = vectors
            .Select(x => new { Id = x.Key, Score = ScalarOracle.DotProduct(query, x.Value) })
            .OrderByDescending(x => x.Score)
            .Take(5)
            .ToList();

        for (int i = 0; i < 5; i++)
        {
            results[i].Id.Should().Be(oracleResults[i].Id);
            // float 부동소수점 오차 허용 범위 내에서 비교 (SIMD와 스칼라의 연산 순서 차이에 따른 오차)
            results[i].Score.Should().BeApproximately(oracleResults[i].Score, 0.0001f);
        }
    }

    [Fact]
    public async Task G0_Upsert_ShouldThrow_OnInvalidDimension()
    {
        // Arrange
        string dbPath = $"file::memory:?cache=shared";
        using var store = new SqliteVectorStore(dbPath, _dimensions);
        var badVector = new float[100]; // 1536이 아님

        // Act
        Func<Task> act = async () => await store.UpsertAsync("bad-doc", badVector);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage($"*1536 dimensions*");
    }
}
