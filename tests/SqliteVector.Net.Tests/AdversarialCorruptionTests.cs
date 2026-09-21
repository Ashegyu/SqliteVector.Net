using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using SqliteVector.Net;
using SqliteVector.Net.Exceptions;
using SqliteVector.Net.Storage;
using SqliteVector.Net.Catalog;
using System.Linq;

namespace SqliteVector.Net.Tests;

public class AdversarialCorruptionTests : IAsyncLifetime
{
    private string _testDir = "";
    private VectorDatabaseOptions _options = null!;

    public Task InitializeAsync()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "SqliteVectorNet_CorruptTests_" + Guid.NewGuid().ToString());
        _options = new VectorDatabaseOptions { Dimensions = 2, Metric = VectorMetric.DotProduct, NormalizeVectors = false };
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true); } catch { }
        return Task.CompletedTask;
    }

    private async Task CreateHealthySegment()
    {
        await using var db = await VectorDatabase.OpenAsync(_testDir, _options);
        await db.UpsertAsync("A", new float[] { 1, 0 }, "A_Meta");
        await db.UpsertAsync("B", new float[] { 0, 1 }, "B_Meta");
    }

    private string GetActiveSegmentPath()
    {
        return Directory.GetFiles(_testDir, "segment_*.vec").First();
    }

    // 1. Invalid Magic
    [Fact]
    public async Task Attack3_1_Invalid_Magic_Must_Reject()
    {
        await CreateHealthySegment();
        SegmentCorruptor.MutateHeader(GetActiveSegmentPath(), h => {
            unsafe {
                uint* magicPtr = (uint*)&h;
                *magicPtr = 0xDEADBEEF;
            }
            return h;
        }, fixChecksum: false);

        var act = async () => { await using var db = await VectorDatabase.OpenAsync(_testDir, _options); };
        (await act.Should().ThrowAsync<VectorStoreCorruptionException>())
            .Where(e => e.Kind == CorruptionKind.InvalidMagic);
    }

    // 2. Header checksum mismatch
    [Fact]
    public async Task Attack3_2_Header_Checksum_Mismatch_Must_Reject()
    {
        await CreateHealthySegment();
        SegmentCorruptor.MutateHeader(GetActiveSegmentPath(), h => h, fixChecksum: false, corruptChecksum: true);

        var act = async () => { await using var db = await VectorDatabase.OpenAsync(_testDir, _options); };
        (await act.Should().ThrowAsync<VectorStoreCorruptionException>())
            .Where(e => e.Kind == CorruptionKind.HeaderChecksum);
    }

    // 3. Offset outside file
    [Fact]
    public async Task Attack3_3_Offset_Outside_File_Must_Reject()
    {
        await CreateHealthySegment();
        SegmentCorruptor.MutateHeader(GetActiveSegmentPath(), h => {
            unsafe {
                long* offsetPtr = (long*)((byte*)&h + 56); // VectorRegionOffset
                *offsetPtr = long.MaxValue - 1024;
            }
            return h;
        }, fixChecksum: true);

        var act = async () => { await using var db = await VectorDatabase.OpenAsync(_testDir, _options); };
        (await act.Should().ThrowAsync<VectorStoreCorruptionException>())
            .Where(e => e.Kind == CorruptionKind.InvalidLayout);
    }

    // 4. Offset arithmetic overflow
    [Fact]
    public async Task Attack3_4_Offset_Arithmetic_Overflow_Must_Reject()
    {
        await CreateHealthySegment();
        SegmentCorruptor.MutateHeader(GetActiveSegmentPath(), h => {
            unsafe {
                int* capPtr = (int*)((byte*)&h + 44);      // Capacity
                int* stridePtr = (int*)((byte*)&h + 36);   // VectorStride
                *capPtr = int.MaxValue;
                *stridePtr = int.MaxValue;
            }
            return h;
        }, fixChecksum: true);

        var act = async () => { await using var db = await VectorDatabase.OpenAsync(_testDir, _options); };
        (await act.Should().ThrowAsync<VectorStoreCorruptionException>())
            .Where(e => e.Kind == CorruptionKind.InvalidLayout);
    }

    // 5. Invalid alignment/stride
    [Fact]
    public async Task Attack3_5_Invalid_Alignment_Stride_Must_Reject()
    {
        await CreateHealthySegment();
        SegmentCorruptor.MutateHeader(GetActiveSegmentPath(), h => {
            unsafe {
                int* alignPtr = (int*)((byte*)&h + 40);    // Alignment
                *alignPtr = 48; // Not power of two
            }
            return h;
        }, fixChecksum: true);

        var act = async () => { await using var db = await VectorDatabase.OpenAsync(_testDir, _options); };
        (await act.Should().ThrowAsync<VectorStoreCorruptionException>())
            .Where(e => e.Kind == CorruptionKind.InvalidLayout);
    }

    // 6. Directory/vector overlap
    [Fact]
    public async Task Attack3_6_Directory_Vector_Overlap_Must_Reject()
    {
        await CreateHealthySegment();
        SegmentCorruptor.MutateHeader(GetActiveSegmentPath(), h => {
            unsafe {
                long* dirPtr = (long*)((byte*)&h + 48);    // DirectoryOffset
                long* vecPtr = (long*)((byte*)&h + 56);    // VectorRegionOffset
                *dirPtr = 128;
                *vecPtr = 128; // Overlaps with directory!
            }
            return h;
        }, fixChecksum: true);

        var act = async () => { await using var db = await VectorDatabase.OpenAsync(_testDir, _options); };
        (await act.Should().ThrowAsync<VectorStoreCorruptionException>())
            .Where(e => e.Kind == CorruptionKind.InvalidLayout);
    }

    // 7. Unknown record flags
    [Fact]
    public async Task Attack3_7_Unknown_Record_Flags_Must_Reject()
    {
        await CreateHealthySegment();
        SegmentCorruptor.MutateDirectoryEntry(GetActiveSegmentPath(), 0, e => {
            unsafe {
                byte* flagPtr = (byte*)&e + 16;
                *flagPtr = 0xFF;
            }
            return e;
        });

        var act = async () => { await using var db = await VectorDatabase.OpenAsync(_testDir, _options); };
        (await act.Should().ThrowAsync<VectorStoreCorruptionException>())
            .Where(e => e.Kind == CorruptionKind.UnknownRecordFlags);
    }

    // 8. Live payload CRC bit flip
    [Fact]
    public async Task Attack3_8_Live_Payload_CRC_BitFlip_Must_Hard_Fail()
    {
        await CreateHealthySegment();
        // Mutate payload of Record 0 (Id: "A")
        SegmentCorruptor.FlipPayloadByte(GetActiveSegmentPath(), 0, 1);

        var act = async () => { await using var db = await VectorDatabase.OpenAsync(_testDir, _options); };
        (await act.Should().ThrowAsync<VectorStoreCorruptionException>())
            .Where(e => e.Kind == CorruptionKind.PayloadChecksum);
    }

    // 9. Orphan payload CRC bit flip
    [Fact]
    public async Task Attack3_9_Orphan_Payload_CRC_BitFlip_Must_Ignore()
    {
        await CreateHealthySegment();
        // Mutate payload of Record 2 (which is an orphan, not mapped in DB)
        SegmentCorruptor.FlipPayloadByte(GetActiveSegmentPath(), 2, 1);

        // This should SUCCEED.
        await using var db = await VectorDatabase.OpenAsync(_testDir, _options);
        var results = await db.SearchAsync(new float[] { 1, 0 }, new VectorSearchOptions { TopK = 1 });
        results.First().Id.Should().Be("A");
    }

    // 10. Truncated sealed segment
    [Fact]
    public async Task Attack3_10_Truncated_Sealed_Segment_Must_Reject()
    {
        await CreateHealthySegment();
        string path = GetActiveSegmentPath();
        
        // Force state to sealed in DB
        await using (var con = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_testDir, "knowledge.db")}"))
        {
            await con.OpenAsync();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "UPDATE segments SET state = 1"; // Sealed
            await cmd.ExecuteNonQueryAsync();
        }

        SegmentCorruptor.Truncate(path, 1024); // Cut in half

        var act = async () => { await using var db = await VectorDatabase.OpenAsync(_testDir, _options); };
        (await act.Should().ThrowAsync<VectorStoreCorruptionException>())
            .Where(e => e.Kind == CorruptionKind.TruncatedSegment);
    }

    // 11. Torn active tail
    [Fact]
    public async Task Attack3_11_Torn_Active_Tail_Must_Recover_Prefix()
    {
        await CreateHealthySegment();
        string path = GetActiveSegmentPath();
        
        // Truncate halfway through the second record (B)
        // Header (128) + Dir (100000*32 = 3.2MB) + Vec Region.
        // Wait, default capacity is 100000. So vec region offset is large.
        // Let's just find the exact size from Header.
        byte[] hdr = File.ReadAllBytes(path).Take(128).ToArray();
        long vecRegion = BitConverter.ToInt64(hdr, 56);
        int stride = BitConverter.ToInt32(hdr, 36);
        
        // Remove B from SQLite Catalog so it acts as an uncommitted torn tail
        await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={System.IO.Path.Combine(_testDir, "knowledge.db")}"))
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM vectors WHERE external_id = 'B'";
            await cmd.ExecuteNonQueryAsync();
        }

        // Keep A (record 0) intact, but cut B (record 1) in half
        SegmentCorruptor.Truncate(path, vecRegion + stride + (stride / 2));

        await using var db = await VectorDatabase.OpenAsync(_testDir, _options);
        // B was corrupted (tail), so A should exist but B might be missing if we recover properly.
        var results = await db.SearchAsync(new float[] { 1, 0 }, new VectorSearchOptions { TopK = 2 });
        results.Should().ContainSingle(r => r.Id == "A");
    }

    // 12. Header/StoreContract mismatch
    [Fact]
    public async Task Attack3_12_Header_StoreContract_Mismatch_Must_Reject()
    {
        await CreateHealthySegment();
        // Store Contract: Dimensions = 2
        // Header: Dimensions = 1536
        SegmentCorruptor.MutateHeader(GetActiveSegmentPath(), h => {
            unsafe {
                int* dimPtr = (int*)((byte*)&h + 24);      // Dimensions
                *dimPtr = 1536;
            }
            return h;
        }, fixChecksum: true);

        var act = async () => { await using var db = await VectorDatabase.OpenAsync(_testDir, _options); };
        (await act.Should().ThrowAsync<VectorStoreCorruptionException>())
            .Where(e => e.Kind == CorruptionKind.ContractMismatch);
    }
}
