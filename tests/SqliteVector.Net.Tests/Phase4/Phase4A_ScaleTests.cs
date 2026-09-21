using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using SqliteVector.Net;
using SqliteVector.Net.Exceptions;
using SqliteVector.Net.Catalog;

namespace SqliteVector.Net.Tests.Phase4;

public class Phase4A_ScaleTests : IDisposable
{
    private readonly string _testDir;

    public Phase4A_ScaleTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "SqliteVectorNet_Phase4A_" + Guid.NewGuid());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    // 4.1 Physical rollover
    [Fact]
    public async Task Repeated_Updates_Must_Rollover_By_Physical_Record_Count()
    {
        var options = new VectorDatabaseOptions { Dimensions = 2, SegmentCapacity = 32 };
        await using var db = await VectorDatabase.OpenAsync(_testDir, options);

        // Append 1000 times to the SAME logical ID
        for (int i = 0; i < 1000; i++)
        {
            await db.UpsertAsync("A", new float[] { i, i }, $"Meta_{i}");
        }

        // 1000 records / 32 capacity = 31.25 -> 32 segments!
        var files = Directory.GetFiles(_testDir, "segment_*.vec");
        files.Length.Should().Be(32, "Because 1000 physical appends should create 32 segments of capacity 32");

        // The logical mapping must only point to the latest
        var results = await db.SearchAsync(new float[] { 999, 999 }, new VectorSearchOptions { TopK = 10 });
        results.Should().ContainSingle(r => r.Id == "A");
        results[0].Metadata.Should().Be("Meta_999");
    }
    // 4.2 Search during rollover
    [Fact]
    public async Task Search_During_Segment_Rollover_Must_Not_See_Duplicates()
    {
        var options = new VectorDatabaseOptions { Dimensions = 2, SegmentCapacity = 32 };
        await using var db = await VectorDatabase.OpenAsync(_testDir, options);

        // Fill up to 31 records
        for (int i = 0; i < 31; i++)
        {
            await db.UpsertAsync($"V_{i}", new float[] { i, i }, $"Meta_{i}");
        }
        
        // Spawn search in background
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var searchTask = Task.Run(async () => {
            while (!cts.IsCancellationRequested)
            {
                var results = await db.SearchAsync(new float[] { 31, 31 }, new VectorSearchOptions { TopK = 100 });
                // Check invariants:
                // 1. No duplicates
                var distinctCount = results.Select(r => r.Id).Distinct().Count();
                distinctCount.Should().Be(results.Length, "No duplicate logical vectors should be visible during rollover");
            }
        });

        // Trigger rollover
        for (int i = 31; i < 40; i++)
        {
            await db.UpsertAsync($"V_{i}", new float[] { i, i }, $"Meta_{i}");
            // Also update an old one to see if CAS and snapshot handles it properly
            await db.UpsertAsync("V_10", new float[] { i, i }, "Meta_Update");
        }

        cts.Cancel();
        await searchTask;
    }

    // 4.11 Multi-segment Top-K
    [Fact]
    public async Task Multi_Segment_TopK_Must_Return_Global_TopK_And_No_Old_Versions()
    {
        var options = new VectorDatabaseOptions { Dimensions = 1, SegmentCapacity = 10, Metric = VectorMetric.EuclideanSquared, NormalizeVectors = false };
        await using var db = await VectorDatabase.OpenAsync(_testDir, options);

        // 100 segments, each 10 vectors -> 1000 appends.
        // We will insert 100 logical vectors 10 times each (so each has 10 versions).
        for (int version = 0; version < 10; version++)
        {
            for (int logicalId = 0; logicalId < 100; logicalId++)
            {
                // To distribute the Top 10 across different segments, we insert them interleaved.
                // LogicalId 0, 10, 20... 90 will be the Top 10 (smallest Euclidean distance to 0)
                // We will assign them values close to 0. Others will be > 100.
                
                float val = (logicalId % 10 == 0) ? logicalId + (10 - version)*0.01f : logicalId + 1000;
                await db.UpsertAsync($"V_{logicalId}", new float[] { val }, $"Meta_v{version}");
            }
        }

        var results = await db.SearchAsync(new float[] { 0 }, new VectorSearchOptions { TopK = 10 });
        
        results.Length.Should().Be(10);
        
        // Ensure no old versions are returned (they should all have Meta_v9)
        foreach (var r in results)
        {
            r.Metadata.Should().Be("Meta_v9");
            r.Id.Should().EndWith("0"); // 0, 10, 20...
        }
        
        // Check exact order: 0, 10, 20, 30...
        results[0].Id.Should().Be("V_0");
        results[9].Id.Should().Be("V_90");
    }

    // 4.14 Huge dimension
    [Fact]
    public async Task Huge_Dimension_Guard_Must_Reject_At_Creation()
    {
        var options = new VectorDatabaseOptions { Dimensions = 10_000_000, SegmentCapacity = 100000 };
        
        var act = async () => { await using var db = await VectorDatabase.OpenAsync(_testDir, options); };
        
        // Either it throws an OverflowException or ArgumentOutOfRangeException or InvalidLayout, but it must not allocate!
        await act.Should().ThrowAsync<Exception>();
    }
}
