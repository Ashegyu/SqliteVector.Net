using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using SqliteVector.Net.Exceptions;

namespace SqliteVector.Net.Tests;

public class Attack313Tests : IDisposable
{
    private readonly string _testDir;
    private readonly VectorDatabaseOptions _options;

    public Attack313Tests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "SqliteVectorNet_Attack313_" + Guid.NewGuid());
        Directory.CreateDirectory(_testDir);
        _options = new VectorDatabaseOptions { Dimensions = 2 };
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    private string GetActiveSegmentPath() => Directory.GetFiles(_testDir, "segment_*.vec")[0];

    [Fact]
    public async Task Attack3_13_Catalog_Referenced_Torn_Tail_Must_Reject()
    {
        await using (var db = await VectorDatabase.OpenAsync(_testDir, _options))
        {
            await db.UpsertAsync("A", new float[] { 1, 0 }, "A_Meta");
        }

        string path = GetActiveSegmentPath();
        
        // Truncate the payload of record A
        byte[] hdr = File.ReadAllBytes(path);
        long vecRegion = BitConverter.ToInt64(hdr, 56);
        
        // Truncate right inside the payload of record 0 (A)
        SegmentCorruptor.Truncate(path, vecRegion + 2);

        var act = async () => { await using var db = await VectorDatabase.OpenAsync(_testDir, _options); };
        (await act.Should().ThrowAsync<SqliteVector.Net.Exceptions.VectorStoreCorruptionException>())
            .Where(e => e.Kind == SqliteVector.Net.Exceptions.CorruptionKind.TruncatedSegment);
    }
}
