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

    [Fact]
    public async Task StreamingSearch_Should_Return_Same_Results_As_MMap()
    {
        var options = new VectorDatabaseOptions { Dimensions = 2, Metric = VectorMetric.Cosine, NormalizeVectors = true };
        await using var db = await VectorDatabase.OpenAsync(_testDir, options);

        await db.UpsertAsync("A", new float[] { 1, 0 }, "meta-A");
        await db.UpsertAsync("B", new float[] { 0, 1 }, "meta-B");
        await db.UpsertAsync("C", new float[] { 0.707f, 0.707f }, "meta-C");

        var query = new float[] { 1, 0 }; // A와 1.0, C와 0.707, B와 0.0

        var mmapResults = await db.SearchAsync(query, new VectorSearchOptions { TopK = 2, UseStreamingSearch = false });
        var streamResults = await db.SearchAsync(query, new VectorSearchOptions { TopK = 2, UseStreamingSearch = true });

        mmapResults.Should().HaveCount(2);
        streamResults.Should().HaveCount(2);
        
        mmapResults[0].Id.Should().Be("A");
        mmapResults[1].Id.Should().Be("C");

        streamResults[0].Id.Should().Be("A");
        streamResults[0].Score.Should().BeApproximately(mmapResults[0].Score, 0.001f);
        
        streamResults[1].Id.Should().Be("C");
        streamResults[1].Score.Should().BeApproximately(mmapResults[1].Score, 0.001f);
    }

    [Fact]
    public async Task Compaction_Should_Not_Overwrite_Concurrent_Updates()
    {
        var dbPath = Path.Combine(_testDir, "knowledge.db");
        var options = new VectorDatabaseOptions { Dimensions = 2, Metric = VectorMetric.DotProduct, NormalizeVectors = false };
        await using var db = await VectorDatabase.OpenAsync(_testDir, options);

        await db.UpsertAsync("target", new float[] { 1, 0 }, "meta-v1");

        // 1. 카탈로그 직접 열어서 수동으로 Generation 2로 업데이트 (유저의 Concurrent Update 흉내)
        var catalog = new SqliteVector.Net.Catalog.SqliteCatalog(dbPath);
        using var tx = catalog.BeginTransaction();
        catalog.UpsertVectorLocation(tx, "target", 2, 1, 100, "meta-v2"); // generation=2, dummy 물리 레코드
        tx.Commit();

        // 2. CompactionEngine 실행: 
        // 옛날 Segment(1)을 읽어서 새 Segment(2)로 쓴다고 가정.
        // newHeader.Generation = 1 (Compaction 시작 시점)
        var compactor = new SqliteVector.Net.Storage.CompactionEngine(catalog, dbPath);
        var newHeader = new SqliteVector.Net.Storage.SegmentHeader(1, 1, 2, SqliteVector.Net.Catalog.VectorElementType.Float32, 100, 64);
        
        string oldSegPath = Path.Combine(_testDir, "segment_000001.vec");
        string newSegPath = Path.Combine(_testDir, "segment_000002.vec");
        
        var oldWriter = new SqliteVector.Net.Storage.ActiveSegmentWriter(oldSegPath, new SqliteVector.Net.Storage.SegmentHeader(1, 1, 2, SqliteVector.Net.Catalog.VectorElementType.Float32, 100, 64));
        oldWriter.AppendVector(new float[] { 1, 0 }, 1);
        oldWriter.Dispose();

        // Act: 컴팩션 수행
        compactor.CompactSegment(1, oldSegPath, 2, newSegPath, newHeader);

        // 3. 검증: 컴팩션은 Generation=1 이하만 업데이트했으므로, 
        // 외부에서 갱신한 Generation=2인 "meta-v2" 레코드 포인터가 
        // 새 컴팩션 주소(newSeg=2, newRec=0)로 덮어씌워지지 않고 그대로 유지되어야 함.
        using var checkConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        checkConn.Open();
        using var cmd = checkConn.CreateCommand();
        cmd.CommandText = "SELECT generation, segment_id, record_index FROM vectors WHERE external_id = 'target'";
        using var reader = cmd.ExecuteReader();
        reader.Read();
        long currentGen = reader.GetInt64(0);
        long currentSeg = reader.GetInt64(1);
        int currentRec = reader.GetInt32(2);

        currentGen.Should().Be(2); // 덮어씌워지지 않았으므로 2
        currentSeg.Should().Be(1); // 덮어씌워지지 않았으므로 원래의 더미 1
        currentRec.Should().Be(100);

        compactor.Close();
        catalog.Dispose();
    }
}
