namespace SqliteVector.Net.Tests;

using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using SqliteVector.Net;
using SqliteVector.Net.Catalog;
using SqliteVector.Net.Search;
using SqliteVector.Net.Storage;
using Xunit;

public class VectorDatabaseFacadeTests
{
    private float[] GenerateVector(int dimensions, float fillValue)
    {
        var vec = new float[dimensions];
        Array.Fill(vec, fillValue);
        return vec;
    }

    [Fact]
    public async Task G15_Facade_ShouldHandleCompleteWorkflow()
    {
        string testDir = Path.Combine(Path.GetTempPath(), $"v2_{Guid.NewGuid()}");
        try
        {
            var options = new VectorDatabaseOptions 
            { 
                Dimensions = 128, 
                Metric = VectorMetric.DotProduct 
            };

            await using (var db = await VectorDatabase.OpenAsync(testDir, options))
            {
                // 1. Upsert
                await db.UpsertAsync("user-1", GenerateVector(128, 1.0f), "meta-1");
                await db.UpsertAsync("user-2", GenerateVector(128, 2.0f), "meta-2");
                await db.UpsertAsync("user-3", GenerateVector(128, -1.0f), "meta-3");

                // 2. Search
                var query = GenerateVector(128, 1.0f);
                var results = await db.SearchAsync(query, new VectorSearchOptions { TopK = 2 });

                // 3. 검증
                foreach (var result in results)
                {
                    Console.WriteLine($"Result: {result.Id}, Score: {result.Score}, Meta: {result.Metadata}");
                }
                results.Should().HaveCount(2);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(testDir)) Directory.Delete(testDir, true); }
            catch { /* Ignore locks in test teardown */ }
        }
    }
}
