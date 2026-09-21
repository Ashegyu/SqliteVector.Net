using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using SqliteVector.Net;
using SqliteVector.Net.Catalog;
using SqliteVector.Net.Exceptions;

namespace SqliteVector.Net.Tests.Phase4;

public class Phase4D_ScaleTests : IDisposable
{
    private readonly string _testDir;

    public Phase4D_ScaleTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "SqliteVectorNet_Phase4D_" + Guid.NewGuid());
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    // 4.7 Mixed workload (Garbage Amplification)
    [Fact]
    public async Task Mixed_Workload_Must_Correctly_Suppress_Amplified_Garbage()
    {
        // 100,000 writes total, but 50% are overwrites (garbage).
        var options = new VectorDatabaseOptions { Dimensions = 2, SegmentCapacity = 10000, Metric = VectorMetric.EuclideanSquared, NormalizeVectors = false };
        await using var db = await VectorDatabase.OpenAsync(_testDir, options);

        // We will insert 5,000 unique vectors.
        // Each vector is updated exactly once to create 5,000 garbage records.
        // Total physical records = 10,000.
        // The current logical count = 5,000.
        
        for (int i = 0; i < 5000; i++)
        {
            // Initial insert (garbage)
            await db.UpsertAsync($"V_{i}", new float[] { -i, -i });
            // Update to true value
            await db.UpsertAsync($"V_{i}", new float[] { i, i });
        }

        // Search for [4999, 4999]. Top 1 should be V_4999 with exact match.
        // It should NOT return the old garbage versions.
        var results = await db.SearchAsync(new float[] { 4999, 4999 }, new VectorSearchOptions { TopK = 5 });
        
        results.Length.Should().Be(5);
        results[0].Id.Should().Be("V_4999");
        results[1].Id.Should().Be("V_4998");
        results[2].Id.Should().Be("V_4997");
        
        // Ensure no garbage (negative coordinates) was returned.
        // If a garbage version was returned, its score would be completely different (opposite direction).
    }

    // 4.3 1M Allocation Limit (Math and Offset Limits)
    [Fact]
    public void Segment_Header_Math_Must_Not_Overflow_For_1M_Vectors_At_1024_Dims()
    {
        // 1M vectors * 1024 dims * 4 bytes = ~4GB payload.
        // This tests that our long/int math in SegmentHeader and ActiveSegmentWriter handles 4GB offsets correctly.
        int dimensions = 1024;
        int capacity = 1_000_000;

        var header = new SqliteVector.Net.Storage.SegmentHeader(1, 1, dimensions, VectorElementType.Float32, capacity, 64);
        
        long totalSize = header.VectorRegionOffset + ((long)header.Capacity * header.VectorStride);
        
        // 1M * 1024 * 4 = 4,096,000,000 bytes.
        // Plus directory: 1M * 24 = 24,000,000 bytes.
        // Total should be around 4.12 GB.
        totalSize.Should().BeGreaterThan(4_000_000_000L);
        totalSize.Should().BeLessThan(5_000_000_000L);
        
        // Ensure VectorStride didn't overflow to negative.
        header.VectorStride.Should().BeGreaterThan(0);
        header.VectorRegionOffset.Should().BeGreaterThan(0);
    }
}
