namespace SqliteVector.Net.Catalog;

using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

/// <summary>
/// G6: SQLite Catalog
/// 검색 핫패스(Hot Path)에서 완전히 분리되며, 논리적 ID 매핑, 메타데이터, 트랜잭션 상태 관리만 수행합니다.
/// 문서 7항의 데이터베이스 스키마를 완벽히 구현합니다.
/// </summary>
public sealed class SqliteCatalog : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteCatalog(string dbPath)
    {
        // 24항: SQLite Transaction 및 Crash Recovery 의존
        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        // 7항 스키마 적용
        cmd.CommandText = @"
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS store_info (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS vectors (
                vector_id INTEGER PRIMARY KEY,
                external_id TEXT NOT NULL UNIQUE,
                generation INTEGER NOT NULL,
                segment_id INTEGER NULL,
                record_index INTEGER NULL,
                deleted INTEGER NOT NULL,
                metadata TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS segments (
                segment_id INTEGER PRIMARY KEY,
                generation INTEGER NOT NULL,
                record_count INTEGER NOT NULL,
                vector_count INTEGER NOT NULL,
                state INTEGER NOT NULL,
                file_name TEXT NOT NULL,
                created_utc INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS commits (
                generation INTEGER PRIMARY KEY,
                committed_utc INTEGER NOT NULL
            );
        ";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// G7: Atomic Update Protocol의 핵심 단계
    /// Segment에 Flush가 성공한 후, 물리적 주소를 카탈로그에 기록합니다. (Torn Write 방어)
    /// </summary>
    public void UpsertVectorLocation(
        SqliteTransaction transaction, 
        string externalId, 
        long generation, 
        long segmentId, 
        int recordIndex, 
        string? metadata)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            INSERT INTO vectors (external_id, generation, segment_id, record_index, deleted, metadata)
            VALUES (@id, @gen, @seg, @rec, 0, @meta)
            ON CONFLICT(external_id) DO UPDATE SET
                generation = @gen,
                segment_id = @seg,
                record_index = @rec,
                deleted = 0,
                metadata = @meta;
        ";
        
        cmd.Parameters.AddWithValue("@id", externalId);
        cmd.Parameters.AddWithValue("@gen", generation);
        cmd.Parameters.AddWithValue("@seg", segmentId);
        cmd.Parameters.AddWithValue("@rec", recordIndex);
        cmd.Parameters.AddWithValue("@meta", (object?)metadata ?? DBNull.Value);
        
        cmd.ExecuteNonQuery();
    }

    public SqliteTransaction BeginTransaction()
    {
        return _connection.BeginTransaction();
    }

    /// <summary>
    /// 53항: Top-K가 결정된 후 물리적 주소(Segment, Record)를 가지고 
    /// 논리적 ID와 메타데이터를 단건(또는 배치) 조회합니다. (비용 O(K))
    /// </summary>
    public (string ExternalId, string? Metadata) ResolveMetadata(long segmentId, int recordIndex)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT external_id, metadata FROM vectors WHERE segment_id = @seg AND record_index = @rec AND deleted = 0";
        cmd.Parameters.AddWithValue("@seg", segmentId);
        cmd.Parameters.AddWithValue("@rec", recordIndex);
        using var reader = cmd.ExecuteReader();
        
        if (reader.Read())
        {
            return (
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)
            );
        }
        
        throw new InvalidDataException($"해당 벡터 데이터를 카탈로그에서 찾을 수 없습니다. (Seg:{segmentId}, Rec:{recordIndex})");
    }

    public void DeleteVectorLocation(SqliteTransaction transaction, string externalId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "UPDATE vectors SET deleted = 1 WHERE external_id = @id";
        cmd.Parameters.AddWithValue("@id", externalId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// G12: SIMD 검색 시 제외할 삭제/만료된 레코드 인덱스 해시셋을 반환합니다.
    /// </summary>
    public HashSet<int> GetDeletedRecordIndices(long segmentId)
    {
        var deleted = new HashSet<int>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT record_index FROM vectors WHERE segment_id = @seg AND deleted = 1 AND record_index IS NOT NULL";
        cmd.Parameters.AddWithValue("@seg", segmentId);
        
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            deleted.Add(reader.GetInt32(0));
        }
        return deleted;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
