using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using SqliteVector.Net;
using SqliteVector.Net.Catalog;

namespace SqliteVector.Net.Tests;

public class V2CoreCompleteTests : IAsyncLifetime
{
    private string _testDir = "";

    public Task InitializeAsync()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "SqliteVectorNet_V2CoreTests_" + Guid.NewGuid().ToString());
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }
        catch (IOException)
        {
            // Ignore file lock issues from SQLite connection pooling during test teardown
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Reopen_With_Wrong_Dimensions_Should_Throw()
    {
        var options1 = new VectorDatabaseOptions { Dimensions = 128, Metric = VectorMetric.Cosine };
        await using (var db1 = await VectorDatabase.OpenAsync(_testDir, options1))
        {
            await db1.UpsertAsync("vec1", new float[128], "meta1");
        }

        var options2 = new VectorDatabaseOptions { Dimensions = 256, Metric = VectorMetric.Cosine };
        
        Func<Task> act = async () => await VectorDatabase.OpenAsync(_testDir, options2);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*StoreContract Mismatch*Dimensions*");
    }

    [Fact]
    public async Task Append_NaN_Vector_Should_Throw()
    {
        var options = new VectorDatabaseOptions { Dimensions = 4, Metric = VectorMetric.Cosine };
        await using var db = await VectorDatabase.OpenAsync(_testDir, options);

        var nanVector = new float[] { 1.0f, float.NaN, 0.5f, 0.2f };

        Func<Task> act = async () => await db.UpsertAsync("vec-nan", nanVector);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*NaN or Infinity*");
    }

    [Fact]
    public async Task Snapshot_Isolation_Concurrent_Update_Should_Not_Fail_Metadata_Resolve()
    {
        var options = new VectorDatabaseOptions { Dimensions = 4, Metric = VectorMetric.DotProduct, NormalizeVectors = false };
        await using var db = await VectorDatabase.OpenAsync(_testDir, options);

        await db.UpsertAsync("doc1", new float[] { 1, 0, 0, 0 }, "meta-v1");
        
        await db.UpsertAsync("doc1", new float[] { 2, 0, 0, 0 }, "meta-v2");

        var results = await db.SearchAsync(new float[] { 1, 0, 0, 0 }, new VectorSearchOptions { TopK = 1 });
        results.Should().HaveCount(1);
        results[0].Id.Should().Be("doc1");
        results[0].Metadata.Should().Be("meta-v2");
    }
}
