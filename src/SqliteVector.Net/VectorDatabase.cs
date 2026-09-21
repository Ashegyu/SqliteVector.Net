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
    public bool UseParallelSearch { get; set; } = false;
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
        System.Collections.BitArray liveSet;

        // G7.1 & G8: 검색 시점의 논리적 스냅샷(LiveSet) 캡처
        lock (_syncRoot)
        {
            liveSet = _catalog.GetLiveSet(segmentId: 1, capacity: 100000);
        }

        // 1. Mmap SIMD 스캔 (Lock-free 병렬 스캔, Stale record는 LiveSet에 의해 완벽 차단됨)
        if (searchOptions.UseParallelSearch)
        {
            _searchEngine.SearchParallel(query.Span, buffer, liveSet);
        }
        else
        {
            _searchEngine.Search(query.Span, buffer, liveSet);
        }
        
        var rawResults = buffer.GetSortedResults(); // 반환 타입: Candidate[]
        var finalResults = new VectorSearchResult[rawResults.Length];

        // 2. 53항: Metadata Resolve (O(K))
        // SQLite 조회를 루프당 1회씩 10번(Top-K)만 수행합니다. 문자열 생성도 이 단계에서만 발생합니다.
        for (int i = 0; i < rawResults.Length; i++)
        {
            var (externalId, metadata) = _catalog.ResolveMetadata(segmentId: rawResults[i].SegmentId, recordIndex: rawResults[i].RecordIndex);
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
