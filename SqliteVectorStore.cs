namespace SqliteVector.Net;

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

/// <summary>
/// 순수 C#과 SQLite로 구현된 Zero-Dependency 벡터 스토어
/// </summary>
public class SqliteVectorStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly int _dimensions; 
    private readonly int _byteSize;

    /// <summary>
    /// 벡터 스토어를 초기화합니다.
    /// </summary>
    /// <param name="dbPath">SQLite 파일 경로 (메모리 DB는 "file::memory:?cache=shared" 사용)</param>
    /// <param name="dimensions">사용할 임베딩 차원 수 (예: OpenAI = 1536, E5 = 384)</param>
    public SqliteVectorStore(string dbPath, int dimensions = 384)
    {
        _dimensions = dimensions;
        _byteSize = dimensions * 4;

        _connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Private;Pooling=false");
        _connection.Open();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS vectors (
                id TEXT PRIMARY KEY,
                payload BLOB NOT NULL,
                metadata TEXT
            ) WITHOUT ROWID;
            
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
        ";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 벡터와 메타데이터(원본 텍스트 등)를 저장합니다.
    /// </summary>
    public async Task UpsertAsync(string id, ReadOnlyMemory<float> vector, string? metadata = null, CancellationToken cancellationToken = default)
    {
        if (vector.Length != _dimensions) 
            throw new ArgumentException($"Vector must have {_dimensions} dimensions. Got {vector.Length}.");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO vectors (id, payload, metadata) VALUES (@id, @payload, @metadata)";
        
        var span = vector.Span;
        byte[] blob = new byte[_byteSize];
        MemoryMarshal.Cast<float, byte>(span).CopyTo(blob);

        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@payload", blob);
        cmd.Parameters.AddWithValue("@metadata", (object?)metadata ?? DBNull.Value);
        
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// SIMD를 활용해 가장 유사한 벡터 Top K개를 고속 검색합니다.
    /// </summary>
    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(ReadOnlyMemory<float> queryVector, int topK = 10, CancellationToken cancellationToken = default)
    {
        if (queryVector.Length != _dimensions) 
            throw new ArgumentException($"Vector must have {_dimensions} dimensions.");

        var topKBuffer = new DenseTopKBuffer(topK);
        
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, payload, metadata FROM vectors";
        
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        byte[] buffer = new byte[_byteSize];
        
        while (await reader.ReadAsync(cancellationToken))
        {
            var querySpan = queryVector.Span;
            string id = reader.GetString(0);
            
            // Allocate-free BLOB reading
            reader.GetBytes(1, 0, buffer, 0, _byteSize);
            
            // SIMD 하드웨어 가속 검색
            float score = VectorMath.CalculateSimilarity(querySpan, buffer);
            
            string? metadata = reader.IsDBNull(2) ? null : reader.GetString(2);
            topKBuffer.Add(id, score, metadata);
        }

        return topKBuffer.GetSortedResults();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
