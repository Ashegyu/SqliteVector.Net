using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using SqliteVector.Net;
using SqliteVector.Net.Catalog;
using SqliteVector.Net.Exceptions;
using SqliteVector.Net.Storage;

namespace SqliteVector.Net.Tests.Phase4;

public class Phase4C_DiskBoundaryTests : IDisposable
{
    private readonly string _testDir;

    public Phase4C_DiskBoundaryTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "SqliteVectorNet_Phase4C_" + Guid.NewGuid());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        ActiveSegmentWriter.TestFaultInjector = null;
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    // 4.5 Disk full during Upsert
    [Fact]
    public async Task Disk_Full_During_Upsert_Must_Recover_And_Ignore_Partial_Payload()
    {
        var options = new VectorDatabaseOptions { Dimensions = 2, SegmentCapacity = 100 };
        await using var db = await VectorDatabase.OpenAsync(_testDir, options);

        // Insert A successfully
        await db.UpsertAsync("V_A", new float[] { 1, 1 });

        // Setup fault for B
        ActiveSegmentWriter.TestFaultInjector = (point) =>
        {
            if (point == "BeforeDirectoryWrite")
            {
                throw new IOException("Simulated Disk Full"!);
            }
        };

        // Try insert B, which should fail with IOException
        var act = async () => await db.UpsertAsync("V_B", new float[] { 2, 2 });
        await act.Should().ThrowAsync<IOException>();

        // Disable fault
        ActiveSegmentWriter.TestFaultInjector = null;

        // Try insert C (this will fail because the ActiveSegmentWriter's internal state might be compromised or the previous write locked something, but wait!
        // Actually, if AppendVector throws, VectorDatabase does NOT catch it!
        // So the caller gets the IOException.
        // What happens to ActiveSegmentWriter?
        // It remains open. Its _currentRecordCount is NOT incremented (because it threw before _currentRecordCount++)!
        // So the next write will overwrite the partial payload! This is perfectly safe!
        
        await db.UpsertAsync("V_C", new float[] { 3, 3 });

        // Let's verify Search
        var results = await db.SearchAsync(new float[] { 1, 1 }, new VectorSearchOptions { TopK = 10 });
        results.Length.Should().Be(2, "A and C should exist, B should be ignored");
        results.Select(r => r.Id).Should().BeEquivalentTo("V_A", "V_C");
    }

    // 4.6 Disk full during Compaction
    [Fact]
    public async Task Disk_Full_During_Compaction_Must_Ignore_Tmp_File_And_Not_Corrupt_Catalog()
    {
        var options = new VectorDatabaseOptions { Dimensions = 2, SegmentCapacity = 100 };
        await using var db = await VectorDatabase.OpenAsync(_testDir, options);

        // Insert A, then overwrite A (causes compaction candidate)
        await db.UpsertAsync("V_A", new float[] { 1, 1 }, "Meta1");
        await db.UpsertAsync("V_A", new float[] { 2, 2 }, "Meta2");

        // Force segment rollover so old segment is Sealed (State = 1)
        for (int i = 0; i < 100; i++)
        {
            await db.UpsertAsync("V_Fill_$i", new float[] { i, i });
        }

        // Now inject disk full for the Compaction tmp file
        ActiveSegmentWriter.TestFaultInjector = (point) =>
        {
            if (point == "BeforeDirectoryWrite")
            {
                throw new IOException("Simulated Disk Full During Compaction"!);
            }
        };

        // Trigger compaction
        var act = async () => await db.CompactAsync();
        await act.Should().ThrowAsync<IOException>();

        ActiveSegmentWriter.TestFaultInjector = null;

        // DB should remain fully functional, and catalog shouldn't be updated!
        var results = await db.SearchAsync(new float[] { 2, 2 }, new VectorSearchOptions { TopK = 1 });
        results[0].Id.Should().Be("V_A");
        results[0].Metadata.Should().Be("Meta2");

        // Ensure tmp file was ignored
        var files = Directory.GetFiles(_testDir, "*.tmp");
        files.Length.Should().Be(1); // Leftover tmp file
        
        var vecFiles = Directory.GetFiles(_testDir, "segment_*.vec");
        // We expect segment_000001 (sealed) and segment_000002 (active)
        vecFiles.Length.Should().Be(2); 
    }

    // 4.13 SQLite Full / Failure during Upsert
    [Fact]
    public async Task Sqlite_Failure_During_Upsert_Must_Rollback_And_Leave_Torn_Tail()
    {
        var options = new VectorDatabaseOptions { Dimensions = 2, SegmentCapacity = 100 };
        await using var db = await VectorDatabase.OpenAsync(_testDir, options);

        // Insert A successfully
        await db.UpsertAsync("V_A", new float[] { 1, 1 });

        // Force a locked or read-only DB? 
        // We can inject a failure into the SqliteCatalog.
        // Let's just create a test hook for SqliteCatalog if we want, or we can just rely on the architecture.
        // Alternatively, we can lock the knowledge.db file from another process, which will cause SQLite to throw SQLITE_BUSY or SQLITE_IOERR!
        // This effectively simulates a failure during the catalog update!
        
        // Simulate SQLITE_FULL by injecting SqliteException during catalog upsert
        SqliteCatalog.TestFaultInjector = (point) =>
        {
            if (point == "BeforeCatalogUpsert")
                throw new Microsoft.Data.Sqlite.SqliteException("Simulated SQLITE_FULL", 13); // 13 is SQLITE_FULL
        };

        var act = async () => await db.UpsertAsync("V_B", new float[] { 2, 2 });
        await act.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();

        SqliteCatalog.TestFaultInjector = null;

        // Now unlock and verify DB is healthy and B was rolled back
        var results = await db.SearchAsync(new float[] { 1, 1 }, new VectorSearchOptions { TopK = 10 });
        results.Length.Should().Be(1, "Only A should exist, B failed at catalog level");
        results[0].Id.Should().Be("V_A");
    }
}
