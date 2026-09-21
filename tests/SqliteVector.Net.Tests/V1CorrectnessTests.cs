namespace SqliteVector.Net.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using SqliteVector.Net;
using SqliteVector.Net.Catalog;
using Xunit;

public class V1CorrectnessTests : IDisposable
{
    private readonly int _dimensions = 1536;
    private readonly string _testDir;

    public V1CorrectnessTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"G0_Tests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
    }

    private float[] GenerateRandomVector(int seed, int dimensions)
    {
        var random = new Random(seed);
        var vec = new float[dimensions];
        for (int i = 0; i < dimensions; i++)
        {
            vec[i] = (float)(random.NextDouble() * 2 - 1);
        }
        return vec;
    }

    [Fact]
    public async Task G0_Search_ShouldMatch_ScalarOracleDotProduct()
    {
        var options = new VectorDatabaseOptions { Dimensions = _dimensions, Metric = VectorMetric.DotProduct };
        await using var store = await VectorDatabase.OpenAsync(_testDir, options);

        var vectors = new Dictionary<string, float[]>();
        for (int i = 0; i < 100; i++)
        {
            string id = $"doc-{i}";
            var vec = GenerateRandomVector(i, _dimensions);
            vectors[id] = vec;
            await store.UpsertAsync(id, vec, $"metadata-{i}");
        }

        float[] query = GenerateRandomVector(999, _dimensions);
        var results = await store.SearchAsync(query, new VectorSearchOptions { TopK = 5 });

        results.Should().HaveCount(5);

        var oracleResults = vectors
            .Select(x => new { Id = x.Key, Score = ScalarOracle.DotProduct(query, x.Value) })
            .OrderByDescending(x => x.Score)
            .Take(5)
            .ToList();

        for (int i = 0; i < 5; i++)
        {
            results[i].Id.Should().Be(oracleResults[i].Id);
            results[i].Score.Should().BeApproximately(oracleResults[i].Score, 0.0001f);
        }
    }

    [Fact]
    public async Task G0_Upsert_ShouldThrow_OnInvalidDimension()
    {
        var options = new VectorDatabaseOptions { Dimensions = _dimensions, Metric = VectorMetric.DotProduct };
        await using var store = await VectorDatabase.OpenAsync(_testDir, options);
        var badVector = new float[100]; 

        Func<Task> act = async () => await store.UpsertAsync("bad-doc", badVector);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true);
    }
}
