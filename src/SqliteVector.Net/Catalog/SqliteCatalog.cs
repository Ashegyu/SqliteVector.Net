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
        // 7항 스키마 정의
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
                deleted INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS vector_records (
                segment_id INTEGER NOT NULL,
                record_index INTEGER NOT NULL,
                external_id TEXT NOT NULL,
                metadata TEXT NULL,
                PRIMARY KEY (segment_id, record_index)
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
            INSERT OR IGNORE INTO vector_records (segment_id, record_index, external_id, metadata)
            VALUES (@seg, @rec, @id, @meta);

            INSERT INTO vectors (external_id, generation, segment_id, record_index, deleted)
            VALUES (@id, @gen, @seg, @rec, 0)
            ON CONFLICT(external_id) DO UPDATE SET
                generation = @gen,
                segment_id = @seg,
                record_index = @rec,
                deleted = 0;
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
    /// 53항: Top-K에 대해 논리 주소(Segment, Record)를 기반으로 
    /// 논리 ID와 메타데이터를 O(K) 단계에 단건 조회합니다.
    /// 스냅샷 락 없이도 Immutable vector_records 테이블을 조회하므로 충돌하지 않습니다.
    /// </summary>
    public (string ExternalId, string? Metadata) ResolveMetadata(long segmentId, int recordIndex)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT external_id, metadata FROM vector_records WHERE segment_id = @seg AND record_index = @rec";
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
        
        throw new InvalidDataException($"해당 물리 레코드를 카탈로그에서 찾을 수 없습니다. (Seg:{segmentId}, Rec:{recordIndex})");
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
    /// G7.1: Snapshot Visibility
    /// 현재 카탈로그상에 살아있는(deleted=0) 최신 버전의 물리적 레코드 인덱스만 캡처합니다.
    /// 과거 버전이나 삭제된 레코드는 이 BitArray에 포함되지 않습니다.
    /// </summary>
    public System.Collections.BitArray GetLiveSet(long segmentId, int capacity)
    {
        var liveSet = new System.Collections.BitArray(capacity);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT record_index FROM vectors WHERE segment_id = @seg AND deleted = 0 AND record_index IS NOT NULL";
        cmd.Parameters.AddWithValue("@seg", segmentId);
        
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            liveSet.Set(reader.GetInt32(0), true);
        }
        return liveSet;
    }

    public void SetStoreInfo(string key, string value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO store_info (key, value) VALUES (@key, @val)";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@val", value);
        cmd.ExecuteNonQuery();
    }

    public string? GetStoreInfo(string key)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM store_info WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key);
        var result = cmd.ExecuteScalar();
        return result as string;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
