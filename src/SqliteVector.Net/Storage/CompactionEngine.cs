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

        // 1. 카탈로그에서 살아있는 벡터 조회
        using var cmd = _directConnection.CreateCommand();
        cmd.CommandText = "SELECT external_id, record_index, metadata FROM vectors WHERE segment_id = @seg AND deleted = 0";
        cmd.Parameters.AddWithValue("@seg", oldSegmentId);
        
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string externalId = reader.GetString(0);
            int oldRecordIndex = reader.GetInt32(1);
            string? metadata = reader.IsDBNull(2) ? null : reader.GetString(2);

            // 2. 구 파일(Reader)에서 벡터 복사
            oldReader.ReadVector(oldRecordIndex, buffer);
            
            // 3. 새 파일(Writer)에 Append-Only 기록
            int newRecordIndex = newWriter.AppendVector(buffer, generation: newHeader.Generation);
            
            // 4. SQLite 포인터 원자적 업데이트
            _catalog.UpsertVectorLocation(tx, externalId, newHeader.Generation, newSegmentId, newRecordIndex, metadata);
        }

        // 5. 모든 Live 벡터 이동이 끝나면 트랜잭션 커밋 (구글 가비지 컬렉터의 Copying 방식과 유사)
        tx.Commit();
        
        // 33항에 따라 구 파일 삭제는 Snapshot 참조가 0이 될 때까지 지연되어야 함
        // (이후 File.Delete 호출)
    }

    public void Close()
    {
        _directConnection.Dispose();
    }
}
