# SqliteVector.NET 🚀

> **SQLite for Truth. .NET for Search.**
> 
> A .NET-native embedded vector search engine that combines SQLite transactional metadata with crash-safe immutable vector segments and zero-allocation SIMD search.

## 💡 Why SqliteVector.NET v2.0?

Traditional approaches store vector floats as `BLOB`s inside SQLite. This causes severe bottlenecks due to SQLite page traversals and managed memory allocations (`GetBytes`) in the search hot path.

**SqliteVector.NET v2.0** completely decouples the architecture:
- **SQLite Catalog**: Handles identity (ExternalId), metadata, mapping, and ACID transactions.
- **.NET Vector Engine**: Handles raw storage via Append-Only `.vec` files, utilizing OS-level `MemoryMappedFile` and SIMD (`TensorPrimitives`) for exact scans at Gigabytes per second.

### 🛡️ Production-Grade Invariants & Guarantees

SqliteVector.NET v2.0 has been hardened against extreme adversarial conditions, scale exhaustion, and hardware failures:

- 🏎️ **Zero-Copy SIMD Search**: Bypasses SQLite in the hot path. Uses direct memory pointers (`unsafe` spans) to scan contiguous vector files.
- 🧱 **Crash-Safe Append-Only Storage**: Vectors are appended to immutable physical segments with exact CRC32 checksums. If a hard crash occurs during a disk write, the partial payload (torn-write) is cleanly ignored upon recovery.
- 🔄 **Snapshot Isolation (MVCC)**: Concurrent readers and a background writer **never** block each other. Dedicated connection pooling ensures no `ArgumentOutOfRangeException` or state leaks during high-concurrency cross-segment queries.
- 📉 **Scale & Resource Exhaustion Defense**:
  - Graceful degradation during `Disk Full (ENOSPC)`: Failed payloads or transactions are rolled back atomically, leaving the physical/logical database perfectly healthy.
  - Handled 10,000 concurrent write-storms elegantly via `SemaphoreSlim` backpressure with completely bounded memory.
  - Withstands Cancellation Storms (`CancellationToken`) in the search hot path without leaking `MemoryMapped` reference counts or locking files.
  - Capacity bounds safely guard against OS-level overflows (e.g. attempting to map a 10M dimensional vector).
- 🧹 **Atomic Compaction**: A background engine reclaims physical disk space from logically overwritten vectors. The compaction process uses atomic `.tmp` file renaming, meaning a crash mid-compaction safely aborts without leaving half-baked active segments.
- 🛡️ **Corruption Defense (Unsafe Pointer Guard)**: Strict mathematical boundary checks are performed on segment headers before any `mmap` views are established. File truncation, header poisoning, or invalid payload offsets are safely rejected with `VectorStoreCorruptionException`, averting `AccessViolationException` process crashes.

## 🚀 Quick Start

```csharp
using SqliteVector.Net;
using SqliteVector.Net.Catalog;

// 1. Initialize Database
var options = new VectorDatabaseOptions 
{ 
    Dimensions = 1536, // e.g., OpenAI text-embedding-3-small
    Metric = VectorMetric.Cosine, // Cosine, EuclideanSquared, InnerProduct
    SegmentCapacity = 100_000 // Physical vectors per .vec file
};

// Opens SQLite catalog and MemoryMapped vector segments safely
await using var db = await VectorDatabase.OpenAsync("./knowledge_dir", options);

// 2. Upsert Vector (Atomic)
float[] myVector = GetEmbedding();
await db.UpsertAsync("doc-123", myVector, metadata: "{\"title\":\"Hello V2\"}");

// 3. Search (Zero-Allocation SIMD Scan)
float[] query = GetQuery();
var results = await db.SearchAsync(query, new VectorSearchOptions { TopK = 5 });

foreach (var res in results)
{
    Console.WriteLine($"ID: {res.Id}, Score: {res.Score}, Meta: {res.Metadata}");
}

// 4. Background Compaction
// Clean up overwritten vectors in sealed segments and reclaim space
await db.CompactAsync();
```

## 🏗️ Architecture Under the Hood

### 1. Catalog Phase
When `UpsertAsync` is called, the vector is serialized directly into an `ActiveSegmentWriter` backed by an OS FileStream using `WriteThrough`. Once physically flushed to disk, the SQLite Catalog atomically executes a CAS (Compare-And-Swap) transaction to bind the `ExternalId` to the newly written physical pointer (Segment ID + Record Index). 

### 2. LiveSet Snapshotting
When a `SearchAsync` begins, the VectorDatabase captures a lock-free snapshot of all physical segment pointers alongside a `BitArray` (LiveSet) indicating which physical records are active.

### 3. Execution Phase
The search request is fanned out across all `MemoryMappedSearchEngine` instances. `unsafe` pointers traverse the physical vector floats, masking out dead elements using the snapshotted `LiveSet`. Results are maintained in a thread-safe `DenseTopKBuffer` utilizing zero string allocations. Only the definitive global Top-K identities are lazily resolved from SQLite.
