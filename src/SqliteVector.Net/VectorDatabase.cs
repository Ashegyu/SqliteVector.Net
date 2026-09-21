namespace SqliteVector.Net;

using System;
using System.IO;
using System.Threading.Tasks;
using SqliteVector.Net.Catalog;
using SqliteVector.Net.Storage;
using SqliteVector.Net.Search;
using SqliteVector.Net.Runtime;

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
    public bool UseStreamingSearch { get; set; } = false;
}

/// <summary>
/// 71~75항: VectorDatabase Facade
/// </summary>
public sealed class VectorDatabase : IAsyncDisposable
{
    private readonly SqliteCatalog _catalog;
    private readonly ActiveSegmentWriter _writer;
    private readonly VectorDatabaseOptions _options;
    private readonly object _syncRoot = new();

    private volatile SearchSnapshot _currentSnapshot = null!;

    private VectorDatabase(SqliteCatalog catalog, ActiveSegmentWriter writer, SearchSnapshot initialSnapshot, VectorDatabaseOptions options)
    {
        _catalog = catalog;
        _writer = writer;
        _currentSnapshot = initialSnapshot;
        _options = options;
    }

    public static async Task<VectorDatabase> OpenAsync(string directoryPath, VectorDatabaseOptions options)
    {
        Directory.CreateDirectory(directoryPath);
        string dbPath = Path.Combine(directoryPath, "knowledge.db");
        string vecPath = Path.Combine(directoryPath, "segment_000001.vec");

        var catalog = new SqliteCatalog(dbPath);
        
        string? storedDim = catalog.GetStoreInfo("Dimensions");
        if (storedDim == null)
        {
            catalog.SetStoreInfo("Dimensions", options.Dimensions.ToString());
            catalog.SetStoreInfo("Metric", options.Metric.ToString());
            catalog.SetStoreInfo("NormalizeVectors", options.NormalizeVectors.ToString());
        }
        else
        {
            if (int.Parse(storedDim) != options.Dimensions)
                throw new InvalidOperationException($"StoreContract Mismatch: 기존 Dimensions({storedDim})가 요청된 Dimensions({options.Dimensions})와 다릅니다.");
            
            string storedMetric = catalog.GetStoreInfo("Metric") ?? options.Metric.ToString();
            if (storedMetric != options.Metric.ToString())
                throw new InvalidOperationException($"StoreContract Mismatch: 기존 Metric({storedMetric})이 요청된 Metric({options.Metric})과 다릅니다.");
        }

        var header = new SegmentHeader(1, 1, options.Dimensions, VectorElementType.Float32, capacity: 100000, alignment: 64);
        var writer = new ActiveSegmentWriter(vecPath, header);
        var searchEngine = new MemoryMappedSearchEngine(vecPath, options.Metric, options.NormalizeVectors);
        
        var initialLiveSet = catalog.GetLiveSet(segmentId: 1, capacity: 100000);
        var initialSnapshot = new SearchSnapshot(1, searchEngine, initialLiveSet);

        return await Task.FromResult(new VectorDatabase(catalog, writer, initialSnapshot, options));
    }

    public async Task UpsertAsync(string id, ReadOnlyMemory<float> vector, string? metadata = null)
    {
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
            int recordIndex = _writer.AppendVector(span, generation: 1);
            
            using var tx = _catalog.BeginTransaction();
            _catalog.UpsertVectorLocation(tx, id, 1, 1, recordIndex, metadata);
            tx.Commit();
            
            RefreshSnapshot();
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
            
            RefreshSnapshot();
        }
        await Task.CompletedTask;
    }

    private void RefreshSnapshot()
    {
        var newLiveSet = _catalog.GetLiveSet(segmentId: 1, capacity: 100000);
        var engine = _currentSnapshot.MMapEngine;
        engine.TryAddReference(); // 새로운 스냅샷이 엔진을 참조함
        
        var newSnapshot = new SearchSnapshot(1, engine, newLiveSet);
        var oldSnapshot = _currentSnapshot;
        _currentSnapshot = newSnapshot;
        
        oldSnapshot.Retire();
    }

    public async Task<VectorSearchResult[]> SearchAsync(ReadOnlyMemory<float> query, VectorSearchOptions searchOptions)
    {
        var buffer = new DenseTopKBuffer(searchOptions.TopK);
        SearchSnapshot snapshot;

        do
        {
            snapshot = _currentSnapshot;
        } 
        while (!snapshot.TryAddReference());

        try
        {
            if (searchOptions.UseStreamingSearch)
            {
                unsafe
                {
                    var streamingSearch = new StreamingExactSearch(
                        snapshot.MMapEngine.FilePath, 
                        snapshot.MMapEngine.Header, 
                        snapshot.MMapEngine.DirectoryPtr, 
                        _options.Metric);
                        
                    streamingSearch.Search(query.Span, buffer, snapshot.LiveSet);
                }
            }
            else if (searchOptions.UseParallelSearch)
            {
                snapshot.MMapEngine.SearchParallel(query.Span, buffer, snapshot.LiveSet);
            }
            else
            {
                snapshot.MMapEngine.Search(query.Span, buffer, snapshot.LiveSet);
            }
            
            var rawResults = buffer.GetSortedResults();
            var finalResults = new VectorSearchResult[rawResults.Length];

            for (int i = 0; i < rawResults.Length; i++)
            {
                var (externalId, metadata) = _catalog.ResolveMetadata(segmentId: rawResults[i].SegmentId, recordIndex: rawResults[i].RecordIndex);
                finalResults[i] = new VectorSearchResult(externalId, rawResults[i].Score, metadata);
            }

            return await Task.FromResult(finalResults);
        }
        finally
        {
            snapshot.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _currentSnapshot.Dispose();
        _writer.Dispose();
        _catalog.Dispose();
        
        await Task.CompletedTask;
    }
}
