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
        string vecPath = Path.Combine(directoryPath, "segment_000001.vec"); // 초기 단일 세그먼트 고정

        var catalog = new SqliteCatalog(dbPath);
        
        // G1: Store Contract Validation
        string? storedDim = catalog.GetStoreInfo("Dimensions");
        if (storedDim == null)
        {
            // 신규 DB: Contract 저장
            catalog.SetStoreInfo("Dimensions", options.Dimensions.ToString());
            catalog.SetStoreInfo("Metric", options.Metric.ToString());
            catalog.SetStoreInfo("NormalizeVectors", options.NormalizeVectors.ToString());
        }
        else
        {
            // 기존 DB: Contract 검증
            if (int.Parse(storedDim) != options.Dimensions)
                throw new InvalidOperationException($"StoreContract Mismatch: 기존 Dimensions({storedDim})가 요청된 Dimensions({options.Dimensions})와 다릅니다.");
            
            string storedMetric = catalog.GetStoreInfo("Metric") ?? options.Metric.ToString();
            if (storedMetric != options.Metric.ToString())
                throw new InvalidOperationException($"StoreContract Mismatch: 기존 Metric({storedMetric})이 요청된 Metric({options.Metric})과 다릅니다.");
        }

        // V2.0 하드코딩 레이아웃 설정
        var header = new SegmentHeader(1, 1, options.Dimensions, VectorElementType.Float32, capacity: 100000, alignment: 64);
        var writer = new ActiveSegmentWriter(vecPath, header);
        var searchEngine = new MemoryMappedSearchEngine(vecPath, options.Metric, options.NormalizeVectors);

        return await Task.FromResult(new VectorDatabase(catalog, writer, searchEngine, options));
    }

    public async Task UpsertAsync(string id, ReadOnlyMemory<float> vector, string? metadata = null)
    {
        // 48항: Normalize on write (계약 준수)
        float[]? normalizedArray = null;
        ReadOnlySpan<float> span = vector.Span;
        
        if (_options.NormalizeVectors && _options.Metric == VectorMetric.Cosine)
        {
            normalizedArray = new float[span.Length];
            span.CopyTo(normalizedArray);
            VectorMath.Normalize(normalizedArray);
            span = normalizedArray;
        }

        lock (_syncRoot)
        {
            // 1. 디스크에 Append 후 Flush (AppendVector 내에서 NaN 체크)
            int recordIndex = _writer.AppendVector(span, generation: 1);
            
            // 2. SQLite 트랜잭션 커밋 (Atomic Update)
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
