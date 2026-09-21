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
    public int SegmentCapacity { get; set; } = 100000;
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
    private ActiveSegmentWriter _writer;
    private long _activeSegmentId;
    private readonly string _dbDirectory;
    private readonly VectorDatabaseOptions _options;
    private readonly object _syncRoot = new();

    private volatile SearchSnapshot _currentSnapshot = null!;
    
    // A6: Operation Lifetime Gate
    private int _disposeState = 0; // 0 = Running, 1 = Disposing, 2 = Disposed
    private int _activeOperations = 0;

    private void TryEnterOperation()
    {
        if (Volatile.Read(ref _disposeState) != 0)
            throw new ObjectDisposedException(nameof(VectorDatabase));
            
        Interlocked.Increment(ref _activeOperations);
        
        if (Volatile.Read(ref _disposeState) != 0)
        {
            Interlocked.Decrement(ref _activeOperations);
            throw new ObjectDisposedException(nameof(VectorDatabase));
        }
    }

    private void ExitOperation()
    {
        Interlocked.Decrement(ref _activeOperations);
    }

    private VectorDatabase(SqliteCatalog catalog, string dbDirectory, long activeSegmentId, ActiveSegmentWriter writer, SearchSnapshot initialSnapshot, VectorDatabaseOptions options)
    {
        _catalog = catalog;
        _dbDirectory = dbDirectory;
        _activeSegmentId = activeSegmentId;
        _writer = writer;
        _currentSnapshot = initialSnapshot;
        _options = options;
    }

    public static async Task<VectorDatabase> OpenAsync(string directoryPath, VectorDatabaseOptions options)
    {
        Directory.CreateDirectory(directoryPath);
        string dbPath = Path.Combine(directoryPath, "knowledge.db");

        var catalog = new SqliteCatalog(dbPath);
        ActiveSegmentWriter? writer = null;
        var engines = new System.Collections.Generic.List<MemoryMappedSearchEngine>();
        
        try
        {
            using (var tx = catalog.BeginTransaction())
            {
                using var cmd = new Microsoft.Data.Sqlite.SqliteCommand("SELECT format_version, dimensions, element_type, metric, normalization FROM store_contract WHERE singleton = 1", tx.Connection, tx);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                {
                    // New store, create contract atomically
                    using var insertCmd = new Microsoft.Data.Sqlite.SqliteCommand(@"
                        INSERT INTO store_contract (singleton, format_version, dimensions, element_type, metric, normalization) 
                        VALUES (1, 1, @dim, 1, @metric, @norm)", tx.Connection, tx);
                    insertCmd.Parameters.AddWithValue("@dim", options.Dimensions);
                    insertCmd.Parameters.AddWithValue("@metric", (int)options.Metric);
                    insertCmd.Parameters.AddWithValue("@norm", options.NormalizeVectors ? 1 : 0);
                    insertCmd.ExecuteNonQuery();
                }
                else
                {
                    // Existing store, validate contract
                    int format = reader.GetInt32(0);
                    int dim = reader.GetInt32(1);
                    int metric = reader.GetInt32(3);
                    bool norm = reader.GetInt32(4) == 1;

                    if (format != 1)
                        throw new InvalidOperationException("Unsupported format version.");
                    if (dim != options.Dimensions)
                        throw new InvalidOperationException($"StoreContract Mismatch: Stored Dimensions({dim}) != Requested({options.Dimensions}).");
                    if (metric != (int)options.Metric)
                        throw new InvalidOperationException($"StoreContract Mismatch: Stored Metric({metric}) != Requested({(int)options.Metric}).");
                    if (norm != options.NormalizeVectors)
                        throw new InvalidOperationException($"StoreContract Mismatch: Stored NormalizeVectors({norm}) != Requested({options.NormalizeVectors}).");
                }
                tx.Commit();
            }

            // G1: Initial Segment Setup
            var allSegments = catalog.GetAllSegments();
            if (allSegments.Count == 0)
            {
                catalog.CreateSegment();
                allSegments = catalog.GetAllSegments();
            }

            long activeSegmentId = catalog.GetActiveSegmentId();
            string activeVecPath = Path.Combine(directoryPath, $"segment_{activeSegmentId:D6}.vec");
            
            var header = new SegmentHeader(activeSegmentId, 1, options.Dimensions, VectorElementType.Float32, capacity: options.SegmentCapacity, alignment: 64);
            writer = new ActiveSegmentWriter(activeVecPath, header);
            
            var liveSets = new System.Collections.Generic.Dictionary<long, System.Collections.BitArray>();

            foreach (long segId in allSegments)
            {
                string segPath = Path.Combine(directoryPath, $"segment_{segId:D6}.vec");
                var engine = new MemoryMappedSearchEngine(segId, segPath, options.Metric, options.NormalizeVectors, isActive: segId == activeSegmentId);
                engines.Add(engine); // Add to engines immediately so it gets cleaned up if anything throws

                if (engine.Header.Dimensions != options.Dimensions)
                {
                    throw new SqliteVector.Net.Exceptions.VectorStoreCorruptionException(SqliteVector.Net.Exceptions.CorruptionKind.ContractMismatch, segId, segPath, $"Dimensions mismatch. Store requires {options.Dimensions}, segment has {engine.Header.Dimensions}");
                }
                
                var liveSet = catalog.GetLiveSet(segmentId: segId, capacity: options.SegmentCapacity);
                
                // B6: Catalog <-> Physical Reconciliation
                engine.ValidateLiveRecords(liveSet);
                
                liveSets.Add(segId, liveSet);
            }

            long initialRev = catalog.GetCurrentRevision();
            var initialSnapshot = new SearchSnapshot(initialRev, engines, liveSets);

            return await Task.FromResult(new VectorDatabase(catalog, directoryPath, activeSegmentId, writer, initialSnapshot, options));
        }
        catch
        {
            foreach (var e in engines)
                e.Release();
            writer?.Dispose();
            catalog.Dispose();
            throw;
        }
    }

    public async Task UpsertAsync(string id, ReadOnlyMemory<float> vector, string? metadata = null)
    {
        TryEnterOperation();
        try
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
                using var tx = _catalog.BeginTransaction();
                long nextRevision = _catalog.AllocateNextRevision(tx);
                
                int recordIndex;
                try
                {
                    recordIndex = _writer.AppendVector(span, generation: nextRevision);
                }
                catch (InvalidOperationException) // C1/C2: Capacity exceeded
                {
                    // 1. Seal active segment
                    _catalog.SealSegment(_activeSegmentId, tx);
                    
                    // 2. Create new active segment
                    _activeSegmentId = _catalog.CreateSegment(tx);
                    
                    // 3. Hot-Swap Writer
                    _writer.Dispose();
                    string newVecPath = Path.Combine(_dbDirectory, $"segment_{_activeSegmentId:D6}.vec");
                    var newHeader = new SegmentHeader(_activeSegmentId, 1, _options.Dimensions, VectorElementType.Float32, capacity: _options.SegmentCapacity, alignment: 64);
                    _writer = new SqliteVector.Net.Storage.ActiveSegmentWriter(newVecPath, newHeader);
                    
                    // 4. Retry append
                    recordIndex = _writer.AppendVector(span, generation: nextRevision);
                }
                
                SqliteVector.Net.Storage.FailureInjector.Inject(SqliteVector.Net.Storage.StorageFailurePoint.AfterPhysicalCommitBeforeLogicalCommit);
                
                _catalog.UpsertVectorLocation(tx, id, nextRevision, _activeSegmentId, recordIndex, metadata);

                SqliteVector.Net.Storage.FailureInjector.Inject(SqliteVector.Net.Storage.StorageFailurePoint.InsideLogicalTransaction);

                tx.Commit();
                
                SqliteVector.Net.Storage.FailureInjector.Inject(SqliteVector.Net.Storage.StorageFailurePoint.AfterLogicalCommitBeforeSnapshotPublish);
                
                RefreshSnapshot();
            }
        }
        finally
        {
            ExitOperation();
        }
        await Task.CompletedTask;
    }

    public async Task DeleteAsync(string id)
    {
        TryEnterOperation();
        try
        {
            lock (_syncRoot)
            {
                using var tx = _catalog.BeginTransaction();
                
                // Allocate revision for the logical tombstone
                long nextRevision = _catalog.AllocateNextRevision(tx);
                
                _catalog.DeleteVectorLocation(tx, id, nextRevision);
                tx.Commit();
                
                RefreshSnapshot();
            }
        }
        finally
        {
            ExitOperation();
        }
        await Task.CompletedTask;
    }

    public Task CompactAsync()
    {
        TryEnterOperation();
        try
        {
            var dbPath = Path.Combine(_dbDirectory, "knowledge.db");
            var compactor = new SqliteVector.Net.Storage.CompactionEngine(_catalog, dbPath);
            try
            {
                compactor.RunCompaction(_dbDirectory, _options);
            }
            finally
            {
                compactor.Close();
            }
            
            RefreshSnapshot();
        }
        finally
        {
            ExitOperation();
        }
        return Task.CompletedTask;
    }

    private void RefreshSnapshot()
    {
        long currentRev = _catalog.GetCurrentRevision();
        var allSegments = _catalog.GetAllSegments();
        var oldSnapshot = _currentSnapshot;
        
        var newEngines = new System.Collections.Generic.List<MemoryMappedSearchEngine>();
        var newLiveSets = new System.Collections.Generic.Dictionary<long, System.Collections.BitArray>();
        
        foreach (var segId in allSegments)
        {
            MemoryMappedSearchEngine? engineToUse = null;
            bool isNewEngine = false;

            // Try to find it in oldSnapshot
            foreach (var oldEngine in oldSnapshot.Engines)
            {
                if (oldEngine.SegmentId == segId)
                {
                    engineToUse = oldEngine;
                    break;
                }
            }
            
            if (engineToUse == null)
            {
                // Must be a new segment just created or the active segment
                string segPath = Path.Combine(_dbDirectory, $"segment_{segId:D6}.vec");
                engineToUse = new MemoryMappedSearchEngine(segId, segPath, _options.Metric, _options.NormalizeVectors, isActive: segId == _activeSegmentId);
                isNewEngine = true;
                
                if (engineToUse.Header.Dimensions != _options.Dimensions)
                {
                    engineToUse.Release();
                    throw new SqliteVector.Net.Exceptions.VectorStoreCorruptionException(SqliteVector.Net.Exceptions.CorruptionKind.ContractMismatch, segId, segPath, $"Dimensions mismatch. Store requires {_options.Dimensions}, segment has {engineToUse.Header.Dimensions}");
                }
            }
            
            if (!isNewEngine)
            {
                engineToUse.TryAddReference(); // Increment ref count because new snapshot will own it
            }
            newEngines.Add(engineToUse);
            var liveSet = _catalog.GetLiveSet(segmentId: segId, capacity: _options.SegmentCapacity);
            
            engineToUse.ValidateLiveRecords(liveSet);
            
            newLiveSets.Add(segId, liveSet);
            Console.WriteLine($"[RefreshSnapshot] Loaded segment {segId} with {engineToUse.Header.Capacity} records, LiveSet bits set: {liveSet.Cast<bool>().Count(b => b)}");
        }
        
        var newSnapshot = new SearchSnapshot(currentRev, newEngines, newLiveSets);
        _currentSnapshot = newSnapshot;
        
        oldSnapshot.Retire();
    }

    public async Task<VectorSearchResult[]> SearchAsync(ReadOnlyMemory<float> query, VectorSearchOptions? searchOptions = null, CancellationToken cancellationToken = default)
    {
        TryEnterOperation();
        try
        {
            var opts = searchOptions ?? new VectorSearchOptions();
            ReadOnlySpan<float> querySpan = query.Span;
            float[]? normalizedQuery = null;
            
            // A4: Query Normalization Parity & Zero-Vector Policy
            if (_options.Metric == VectorMetric.Cosine && _options.NormalizeVectors)
            {
                float magSq = 0;
                for (int i = 0; i < querySpan.Length; i++) magSq += querySpan[i] * querySpan[i];
                if (magSq == 0)
                    throw new ArgumentException("Zero vector is invalid for normalized Cosine search.");
                    
                normalizedQuery = new float[querySpan.Length];
                querySpan.CopyTo(normalizedQuery);
                VectorMath.Normalize(normalizedQuery);
                querySpan = normalizedQuery;
            }

            var buffer = new DenseTopKBuffer(opts.TopK);
            SearchSnapshot snapshot;

            do
            {
                snapshot = _currentSnapshot;
            } 
            while (!snapshot.TryAddReference());

            try
            {
                foreach (var engine in snapshot.Engines)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var liveSet = snapshot.LiveSets.TryGetValue(engine.SegmentId, out var ls) ? ls : null;

                    if (opts.UseStreamingSearch)
                    {
                        unsafe
                        {
                            var streamingSearch = new StreamingExactSearch(
                                engine.SegmentId,
                                engine.FilePath, 
                                engine.Header, 
                                engine.DirectoryPtr, 
                                _options.Metric,
                                _options.NormalizeVectors);
                                
                            streamingSearch.Search(querySpan, buffer, liveSet);
                        }
                    }
                    else if (opts.UseParallelSearch)
                    {
                        engine.SearchParallel(querySpan, buffer, liveSet);
                    }
                    else
                    {
                        engine.Search(querySpan, buffer, liveSet);
                    }
                }
                
                var rawResults = buffer.GetSortedResults();
                var finalResults = new VectorSearchResult[rawResults.Length];

                for (int i = 0; i < rawResults.Length; i++)
                {
                    var (externalId, metadata) = _catalog.ResolveMetadata(segmentId: rawResults[i].SegmentId, recordIndex: rawResults[i].RecordIndex);
                    finalResults[i] = new VectorSearchResult(externalId, rawResults[i].Score, metadata);
                }

                return finalResults;
            }
            finally
            {
                snapshot.Dispose();
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;
        
        while (Volatile.Read(ref _activeOperations) > 0)
        {
            await Task.Delay(1);
        }
        
        _currentSnapshot.Retire();
        _writer.Dispose();
        _catalog.Dispose();
        
        Volatile.Write(ref _disposeState, 2);
    }
}
