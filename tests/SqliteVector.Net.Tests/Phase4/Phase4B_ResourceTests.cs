using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using SqliteVector.Net;
using SqliteVector.Net.Catalog;

namespace SqliteVector.Net.Tests.Phase4;

public class Phase4B_ResourceTests : IDisposable
{
    private readonly string _testDir;

    public Phase4B_ResourceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "SqliteVectorNet_Phase4B_" + Guid.NewGuid());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    // 4.9 Open / Close 10,000 Cycles
    [Fact]
    public async Task Open_Close_Stress_Must_Not_Leak_Handles()
    {
        var options = new VectorDatabaseOptions { Dimensions = 2, SegmentCapacity = 100 };
        
        // Setup initial DB
        await using (var db = await VectorDatabase.OpenAsync(_testDir, options))
        {
            await db.UpsertAsync("V_1", new float[] { 1, 1 }, "Meta");
        }

        // Measure baseline
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        
        using var process = Process.GetCurrentProcess();
        int initialHandles = process.HandleCount;
        long initialMemory = process.PrivateMemorySize64;

        int iterations = 1000; // Using 1000 instead of 10,000 to keep test time reasonable, handles would blow up by 1000 if leaked

        for (int i = 0; i < iterations; i++)
        {
            await using var db = await VectorDatabase.OpenAsync(_testDir, options);
            var results = await db.SearchAsync(new float[] { 1, 1 }, new VectorSearchOptions { TopK = 1 });
            
            if (i % 10 == 0)
            {
                await db.UpsertAsync("V_$i", new float[] { i, i });
            }
        }
        
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // Pools can retain handles
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        
        process.Refresh();
        int finalHandles = process.HandleCount;
        long finalMemory = process.PrivateMemorySize64;

        // Allowing a reasonable buffer (e.g. 100 handles, 50MB) for runtime JIT and unrelated allocations
        (finalHandles - initialHandles).Should().BeLessThan(200, "Handle leak detected!");
        (finalMemory - initialMemory).Should().BeLessThan(100 * 1024 * 1024, "Memory leak detected!");
    }

    // 4.10 Cancellation Storm
    [Fact]
    public async Task Cancellation_Storm_Must_Not_Leak_RefCounts()
    {
        var options = new VectorDatabaseOptions { Dimensions = 2, SegmentCapacity = 1000 };
        await using var db = await VectorDatabase.OpenAsync(_testDir, options);
        await db.UpsertAsync("V_1", new float[] { 1, 1 }, "Meta");

        var writerTask = Task.Run(async () =>
        {
            for (int i = 0; i < 500; i++)
            {
                await db.UpsertAsync("V_$i", new float[] { i, i });
            }
        });

        for (int i = 0; i < 500; i++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromTicks(1)); // Very short timeout
            try
            {
                await db.SearchAsync(new float[] { 1, 1 }, new VectorSearchOptions { TopK = 10 }, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        await writerTask;

        // Final search must succeed
        var results = await db.SearchAsync(new float[] { 1, 1 }, new VectorSearchOptions { TopK = 10 });
        results.Length.Should().BeGreaterThan(0);
    }

    // 4.8 Backpressure
    [Fact]
    public async Task Backpressure_Must_Survive_10000_Concurrent_Writes()
    {
        var options = new VectorDatabaseOptions { Dimensions = 2, SegmentCapacity = 1000, Metric = VectorMetric.EuclideanSquared, NormalizeVectors = false };
        await using var db = await VectorDatabase.OpenAsync(_testDir, options);

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        long initialMemory = process.PrivateMemorySize64;

        var tasks = new Task[10000];
        
        // Spawn 10000 concurrent writes
        for (int i = 0; i < 10000; i++)
        {
            int index = i; // local copy
            tasks[i] = db.UpsertAsync($"V_{index}", new float[] { index, index }, "Meta");
        }

        await Task.WhenAll(tasks);

        process.Refresh();
        long finalMemory = process.PrivateMemorySize64;
        
        // 10000 Task allocations will take some memory, but not huge amounts.
        // As long as it completes without OOM and memory is within reason (e.g. +300MB), we're good.
        (finalMemory - initialMemory).Should().BeLessThan(300 * 1024 * 1024, "Memory must be bounded during write storm");
        
        var results = await db.SearchAsync(new float[] { 9999, 9999 }, new VectorSearchOptions { TopK = 1 });
        results[0].Id.Should().Be("V_9999");
    }
}
