namespace SqliteVector.Net;

using System;
using System.IO;
using System.Threading.Tasks;
using SqliteVector.Net.Catalog;
using SqliteVector.Net.Storage;
using SqliteVector.Net.Search;

public class VectorDatabaseOptions
{
    public int Dimensions { get; set; }
    public VectorMetric Metric { get; set; } = VectorMetric.Cosine;
    public bool NormalizeVectors { get; set; } = true;
}

public class VectorSearchOptions
{
    public int TopK { get; set; } = 10;
}

/// <summary>
/// 71~75항: VectorDatabase Facade
/// 복잡한 백엔드 스토리지를 캡슐화하고 사용자를 위한 깔끔한 진입점을 제공합니다.
/// </summary>
public sealed class VectorDatabase : IAsyncDisposable
{
    private readonly SqliteCatalog _catalog;
    private readonly ActiveSegmentWriter _writer;
    private readonly MemoryMappedSearchEngine _searchEngine;
    private readonly VectorDatabaseOptions _options;
    private readonly object _syncRoot = new();

    private VectorDatabase(SqliteCatalog catalog, ActiveSegmentWriter writer, MemoryMappedSearchEngine searchEngine, VectorDatabaseOptions options)
    {
        _catalog = catalog;
        _writer = writer;
        _searchEngine = searchEngine;
        _options = options;
    }

    public static async Task<VectorDatabase> OpenAsync(string directoryPath, VectorDatabaseOptions options)
    {
        Directory.CreateDirectory(directoryPath);
        string dbPath = Path.Combine(directoryPath, "knowledge.db");
        string vecPath = Path.Combine(directoryPath, "segment_000001.vec"); // 초기 버전은 단일 파일

        var catalog = new SqliteCatalog(dbPath);
        
        // V2.0 파일 헤더 설정
        var header = new SegmentHeader(1, 1, options.Dimensions, VectorElementType.Float32, capacity: 100000, alignment: 64);
        var writer = new ActiveSegmentWriter(vecPath, header);
        var searchEngine = new MemoryMappedSearchEngine(vecPath, options.Metric);

        return await Task.FromResult(new VectorDatabase(catalog, writer, searchEngine, options));
    }

    public async Task UpsertAsync(string id, ReadOnlyMemory<float> vector, string? metadata = null)
    {
        // 48항: Normalize on write (선택사항)
        if (_options.NormalizeVectors && _options.Metric == VectorMetric.Cosine)
        {
            // 실제 구현에서는 벡터를 복사한 뒤 노말라이즈 처리 (현재는 생략)
        }

        lock (_syncRoot)
        {
            // 1. 디스크에 벡터 Append 및 Flush
            int recordIndex = _writer.AppendVector(vector.Span, generation: 1);
            
            // 2. SQLite 트랜잭션으로 원자적 커밋 (Atomic Update)
            using var tx = _catalog.BeginTransaction();
            _catalog.UpsertVectorLocation(tx, id, 1, 1, recordIndex, metadata);
            tx.Commit();
        }

        await Task.CompletedTask;
    }

    public async Task DeleteAsync(string id)
    {
        lock (_syncRoot)
        {
            using var tx = _catalog.BeginTransaction();
            _catalog.DeleteVectorLocation(tx, id);
            tx.Commit();
        }
        await Task.CompletedTask;
    }

    public async Task<VectorSearchResult[]> SearchAsync(ReadOnlyMemory<float> query, VectorSearchOptions searchOptions)
    {
        var buffer = new DenseTopKBuffer(searchOptions.TopK);
        
        // G12: 삭제된 인덱스 가져오기 (O(D) 비용 - D는 삭제된 건수)
        var deletedIndices = _catalog.GetDeletedRecordIndices(segmentId: 1);

        // 1. Mmap SIMD 스캔 (SQLite 개입 완전 제로, 디스크 I/O 최적화)
        _searchEngine.Search(query.Span, buffer, deletedIndices);
        
        var rawResults = buffer.GetSortedResults();
        var finalResults = new VectorSearchResult[rawResults.Length];

        // 2. 53항: Metadata Resolve (O(K))
        // Top-K(예: 10개)로 좁혀진 결과에 대해서만 SQLite 조회를 수행하여 오버헤드 최소화
        for (int i = 0; i < rawResults.Length; i++)
        {
            int recordIndex = int.Parse(rawResults[i].Id);
            var (externalId, metadata) = _catalog.ResolveMetadata(segmentId: 1, recordIndex: recordIndex);
            
            finalResults[i] = new VectorSearchResult(externalId, rawResults[i].Score, metadata);
        }

        return await Task.FromResult(finalResults);
    }

    public async ValueTask DisposeAsync()
    {
        // 69항: 안전한 리소스 해제 순서 준수
        _searchEngine.Dispose();
        _writer.Dispose();
        _catalog.Dispose();
        
        await Task.CompletedTask;
    }
}
