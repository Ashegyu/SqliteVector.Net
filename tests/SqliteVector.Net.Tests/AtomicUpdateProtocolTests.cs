namespace SqliteVector.Net.Tests;

using System;
using System.IO;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using SqliteVector.Net.Catalog;
using SqliteVector.Net.Storage;
using Xunit;

public class AtomicUpdateProtocolTests
{
    [Fact]
    public void G7_AtomicUpdate_ShouldCommitSegmentAndSQLiteTogether()
    {
        string testVecFile = Path.Combine(Path.GetTempPath(), $"atomic_{Guid.NewGuid()}.vec");
        string testDbFile = Path.Combine(Path.GetTempPath(), $"atomic_{Guid.NewGuid()}.db");
        
        try
        {
            var header = new SegmentHeader(1, 1, 10, VectorElementType.Float32, 100, 32);
            var writer = new ActiveSegmentWriter(testVecFile, header);
            var catalog = new SqliteCatalog(testDbFile);

            var vector = new float[10];
            vector[0] = 5.5f;

            // 1. Vector 파일에 Append 및 Flush (디스크 레벨 완료 보장)
            int recordIndex = writer.AppendVector(vector, generation: 2);

            // 2. SQLite 트랜잭션 시작 (원자적 업데이트)
            using (var tx = catalog.BeginTransaction())
            {
                catalog.UpsertVectorLocation(
                    transaction: tx,
                    externalId: "doc-atomic",
                    generation: 2,
                    segmentId: 1,
                    recordIndex: recordIndex,
                    metadata: "some-metadata");

                // 3. 트랜잭션 커밋
                tx.Commit();
            }

            // 4. 검증 (SQLite Catalog 상태 조회)
            using (var conn = new SqliteConnection($"Data Source={testDbFile}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT segment_id, record_index FROM vectors WHERE external_id = 'doc-atomic'";
                using var reader = cmd.ExecuteReader();
                
                reader.Read().Should().BeTrue();
                reader.GetInt64(0).Should().Be(1);
                reader.GetInt32(1).Should().Be(0);
            }

            writer.Dispose();
            catalog.Dispose();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(testVecFile)) File.Delete(testVecFile);
            if (File.Exists(testDbFile)) File.Delete(testDbFile);
        }
    }
}
