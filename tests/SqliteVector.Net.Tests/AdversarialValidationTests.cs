using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using SqliteVector.Net;
using SqliteVector.Net.Catalog;
using SqliteVector.Net.Storage;

namespace SqliteVector.Net.Tests;

public class AdversarialValidationTests : IAsyncLifetime
{
    private string _testDir = "";

    public Task InitializeAsync()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "SqliteVectorNet_AdversarialTests_" + Guid.NewGuid().ToString());
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
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Compaction_From_Revision_R_Must_Not_Revert_Record_Updated_At_R_Plus_1()
    {
        var options = new VectorDatabaseOptions { Dimensions = 2, Metric = VectorMetric.DotProduct, NormalizeVectors = false };
        var db = await VectorDatabase.OpenAsync(_testDir, options);

        try
        {
            // 1. 초기 상태 (Revision 10을 만들기 위해 더미 삽입)
            for (int i = 0; i < 9; i++)
            {
                await db.UpsertAsync($"dummy{i}", new float[] { 0, 0 }, "dummy");
            }
            
            await db.UpsertAsync("A", new float[] { 1, 0 }, "OLD");

            // 강제로 segment 1을 sealed 처리
            var dbPath = Path.Combine(_testDir, "knowledge.db");
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE segments SET state = 1 WHERE segment_id = 1; INSERT INTO segments (state) VALUES (0);";
                cmd.ExecuteNonQuery();
            }

            // Reopen to refresh _activeSegmentId
            await db.DisposeAsync();
            db = await VectorDatabase.OpenAsync(_testDir, options);

        try
        {
            // 100번 반복 (1000번은 시간 상 xUnit 타임아웃 걸릴 수 있으나 일단 빠르게 100번 수행)
            for (int iteration = 0; iteration < 100; iteration++)
            {
                using var compactionReachedPublish = new ManualResetEventSlim(false);
                using var allowCompactionPublish = new ManualResetEventSlim(false);

                long latestRevisionBeforeCompaction = 0;
                using (var catalog = new SqliteCatalog(dbPath))
                {
                    latestRevisionBeforeCompaction = catalog.GetCurrentRevision();
                }

                // 훅 설정
                CompactionEngine.BeforeCatalogPublishHook = () =>
                {
                    Console.WriteLine($"[Test] Iteration {iteration}: Hook reached. Waiting for allowCompactionPublish...");
                    compactionReachedPublish.Set();
                    allowCompactionPublish.Wait();
                    Console.WriteLine($"[Test] Iteration {iteration}: Hook unblocked.");
                };

                // 2. Compaction 시작 (Background)
                Console.WriteLine($"[Test] Iteration {iteration}: Starting compaction task.");
                var compactTask = Task.Run(async () => 
                {
                    try 
                    {
                        await db.CompactAsync();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Test] Iteration {iteration}: Compaction crashed: {ex}");
                        throw;
                    }
                });

                // 3. Compaction이 원본을 읽고 새 물리 파일에 기록한 직후 대기 중임을 확인
                Console.WriteLine($"[Test] Iteration {iteration}: Waiting for compactionReachedPublish...");
                compactionReachedPublish.Wait();
                Console.WriteLine($"[Test] Iteration {iteration}: compactionReachedPublish set. Starting Upsert.");

                // 4. Concurrent Upsert (R+1)
                float newX = 0f;
                float newY = iteration + 1f; // 각 반복마다 고유한 값
                string newMeta = $"NEW_{iteration}";
                try
                {
                    await db.UpsertAsync("A", new float[] { newX, newY }, newMeta);
                    Console.WriteLine($"[Test] Iteration {iteration}: Upsert complete.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Test] Iteration {iteration}: UpsertAsync crashed: {ex}");
                    throw;
                }
                
                long latestRevisionAfterUpsert = 0;
                using (var catalog = new SqliteCatalog(dbPath))
                {
                    latestRevisionAfterUpsert = catalog.GetCurrentRevision();
                }
                
                latestRevisionAfterUpsert.Should().BeGreaterThan(latestRevisionBeforeCompaction, "Concurrent Update가 버전을 높여야 함");

                // 5. Compaction Publish 속개
                Console.WriteLine($"[Test] Iteration {iteration}: Unblocking compaction...");
                allowCompactionPublish.Set();
                Console.WriteLine($"[Test] Iteration {iteration}: Awaiting compactTask...");
                await compactTask;
                Console.WriteLine($"[Test] Iteration {iteration}: compactTask finished.");

                // 6. 결과 검증 (메모리 상태)
                var results = await db.SearchAsync(new float[] { newX, newY }, new VectorSearchOptions { TopK = 5 });
                var resultsList = results.ToList();
                Console.WriteLine($"[Test] Iteration {iteration}: Found {resultsList.Count} results.");
                foreach(var r in resultsList) {
                    Console.WriteLine($"[Test] Iteration {iteration}: ID={r.Id}, Score={r.Score}");
                }
                resultsList.Should().HaveCountGreaterThan(0);
                resultsList[0].Id.Should().Be("A");
                resultsList[0].Metadata.Should().Be(newMeta);

                // OLD vector 검색 시 등장하지 않아야 함
                var oldResults = await db.SearchAsync(new float[] { 1, 0 }, new VectorSearchOptions { TopK = 1 });
                var oldResultsList = oldResults.ToList();
                if (oldResultsList.Count > 0 && oldResultsList[0].Id == "A")
                {
                    oldResultsList[0].Metadata.Should().NotBe("OLD", "OLD physical copy가 current처럼 보이면 안 됨");
                }
                
                // 후속 루프를 위해 이전 compaction이 만든 새 세그먼트를 sealed로 만듦
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "UPDATE segments SET state = 1 WHERE state = 0; INSERT INTO segments (state) VALUES (0);";
                    cmd.ExecuteNonQuery();
                }

                // VectorDatabase Refresh (Hack: Re-open)
                await db.DisposeAsync();
                db = await VectorDatabase.OpenAsync(_testDir, options);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Test] FATAL ERROR: {ex}");
            throw;
        }
        }
        finally
        {
            await db.DisposeAsync();
        }
        
        CompactionEngine.BeforeCatalogPublishHook = null;

        // 7. 재시작 검증
        await db.DisposeAsync();

        await using var db2 = await VectorDatabase.OpenAsync(_testDir, options);
        var finalResults = await db2.SearchAsync(new float[] { 0, 100 }, new VectorSearchOptions { TopK = 1 });
        finalResults.Should().HaveCount(1);
        finalResults[0].Id.Should().Be("A");
        finalResults[0].Metadata.Should().Be("NEW_99");
        
        // 추가 검증: Stale compaction record는 카탈로그에 deleted=0으로 존재해선 안 된다
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_testDir, "knowledge.db")}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT generation, metadata FROM vector_records vr JOIN vectors v ON vr.segment_id = v.segment_id AND vr.record_index = v.record_index WHERE v.external_id = 'A' AND v.deleted = 0";
            using var reader = cmd.ExecuteReader();
            reader.Read().Should().BeTrue();
            reader.GetString(1).Should().Be("NEW_99");
            // 행이 단 하나여야 함
            reader.Read().Should().BeFalse();
        }
    }
}
