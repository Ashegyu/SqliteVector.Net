namespace SqliteVector.Net.Storage;

using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using SqliteVector.Net.Catalog;

/// <summary>
/// G13: Compaction Engine
/// V2 아키텍처의 유일한 Garbage Collector입니다. 
/// 파편화된 구형 세그먼트에서 삭제/만료(Obsolete)된 벡터를 버리고,
/// 살아있는(Live) 벡터만 새 세그먼트 파일로 순차 복사하여 용량을 회수합니다.
/// </summary>
public sealed class CompactionEngine
{
    private readonly SqliteCatalog _catalog;
    private readonly SqliteConnection _directConnection;

    public CompactionEngine(SqliteCatalog catalog, string dbPath)
    {
        _catalog = catalog;
        _directConnection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        _directConnection.Open();
    }

    public void CompactSegment(long oldSegmentId, string oldSegmentPath, long newSegmentId, string newSegmentPath, SegmentHeader newHeader)
    {
        using var oldReader = new SegmentReader(oldSegmentPath);
        using var newWriter = new ActiveSegmentWriter(newSegmentPath, newHeader);

        using var tx = _catalog.BeginTransaction();
        Span<float> buffer = stackalloc float[newHeader.Dimensions];
        
        long compactionGeneration = newHeader.Generation;

        // 1. 카탈로그에서 현재(compactionGeneration 이하) 유효한 논리 데이터 조회
        using var cmd = _directConnection.CreateCommand();
        cmd.CommandText = @"
            SELECT v.external_id, v.record_index, r.metadata 
            FROM vectors v
            LEFT JOIN vector_records r ON v.segment_id = r.segment_id AND v.record_index = r.record_index
            WHERE v.segment_id = @seg AND v.deleted = 0 AND v.generation <= @gen";
        cmd.Parameters.AddWithValue("@seg", oldSegmentId);
        cmd.Parameters.AddWithValue("@gen", compactionGeneration);
        
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string externalId = reader.GetString(0);
            int oldRecordIndex = reader.GetInt32(1);
            string? metadata = reader.IsDBNull(2) ? null : reader.GetString(2);

            // 2. 물리 세그먼트에서 원본 벡터 읽기
            oldReader.ReadVector(oldRecordIndex, buffer);
            
            // 3. 새 물리 세그먼트에 Append-Only 복사
            int newRecordIndex = newWriter.AppendVector(buffer, generation: compactionGeneration);
            
            // 4. SQLite 데이터베이스에 새 주소 반영 (단, 백그라운드 작업 중에 유저가 업데이트했다면 덮어쓰지 않음!)
            using var updateCmd = _directConnection.CreateCommand();
            updateCmd.Transaction = tx;
            updateCmd.CommandText = @"
                UPDATE vectors 
                SET segment_id = @newSeg, record_index = @newRec, generation = @newGen
                WHERE external_id = @id AND generation <= @compactionGen
            ";
            updateCmd.Parameters.AddWithValue("@newSeg", newSegmentId);
            updateCmd.Parameters.AddWithValue("@newRec", newRecordIndex);
            updateCmd.Parameters.AddWithValue("@newGen", compactionGeneration);
            updateCmd.Parameters.AddWithValue("@id", externalId);
            updateCmd.Parameters.AddWithValue("@compactionGen", compactionGeneration);
            
            int rowsAffected = updateCmd.ExecuteNonQuery();
            
            // rowsAffected == 0 인 경우: 
            // 컴팩션이 진행되는 동안(백그라운드), 유저가 이 벡터를 새롭게 Upsert 했음을 의미합니다.
            // 새 데이터가 우선순위를 가지므로, 방금 복사한 구버전 벡터는 고아가 되며 다음 컴팩션에서 제거됩니다.
            
            // 4.5. 메타데이터 히스토리 테이블에도 새 물리 주소 등록 (ResolveMetadata 용)
            if (rowsAffected > 0)
            {
                using var historyCmd = _directConnection.CreateCommand();
                historyCmd.Transaction = tx;
                historyCmd.CommandText = "INSERT OR IGNORE INTO vector_records (segment_id, record_index, external_id, metadata) VALUES (@seg, @rec, @id, @meta)";
                historyCmd.Parameters.AddWithValue("@seg", newSegmentId);
                historyCmd.Parameters.AddWithValue("@rec", newRecordIndex);
                historyCmd.Parameters.AddWithValue("@id", externalId);
                historyCmd.Parameters.AddWithValue("@meta", (object?)metadata ?? DBNull.Value);
                historyCmd.ExecuteNonQuery();
            }
        }

        // 5. 모든 Live 벡터 이동이 완료되면 원자적 커밋 (유저는 이 순간부터 새 세그먼트에서 검색)
        tx.Commit();
        
        // 33항에 의거, 이전 스냅샷의 참조 카운트가 0이 되면 구버전 파일(oldSegmentPath)이 삭제됩니다.
    }

    public void Close()
    {
        _directConnection.Dispose();
    }
}
