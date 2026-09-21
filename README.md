# SqliteVector.NET 🚀

> **SQLite for truth. .NET for search.**
> 
> A .NET-native embedded vector search engine that combines SQLite transactional metadata with crash-safe immutable vector segments and zero-allocation SIMD search.

## 🌟 Why SqliteVector.NET v2.0?

Traditional approaches store vector floats as `BLOB`s inside SQLite. This causes severe bottlenecks due to SQLite page traversals and managed memory allocations (`GetBytes`) in the search hot path.

**SqliteVector.NET v2.0** completely decouples the architecture:
- **SQLite** handles identity, metadata, transactions, and logical mappings.
- **.NET Vector Engine** handles raw storage via Append-Only `.vec` files, utilizing OS-level `MemoryMappedFile` and SIMD `TensorPrimitives` for exact scans at GB/s.

### ✨ Key Features
- 🚀 **Zero-Copy SIMD Search**: Bypasses SQLite in the hot path. Uses direct memory pointers to scan contiguous vector files with `AVX2`/`AVX-512`.
- 🛡️ **Crash-Safe Append-Only Storage**: Vectors are appended to immutable segments with CRC32 hashes, completely eliminating torn-writes.
- 🔄 **Snapshot Isolation (MVCC)**: Concurrent readers and a background writer never block each other.
- 💾 **Out-of-Core Streaming**: Seamlessly fallback to `SequentialScan` streaming for datasets larger than available RAM.
- 🧹 **Background Compaction**: Automatically reclaims physical disk space from logically deleted vectors.

## 🚀 Quick Start

```csharp
using SqliteVector.Net;
using SqliteVector.Net.Catalog;

// 1. Initialize Database
var options = new VectorDatabaseOptions 
{ 
    Dimensions = 1536, // e.g., OpenAI text-embedding-3-small
    Metric = VectorMetric.Cosine 
};

// Opens SQLite catalog and MemoryMapped vector segments
await using var db = await VectorDatabase.OpenAsync("./knowledge_dir", options);

// 2. Upsert Vector
float[] myVector = GetEmbedding();
await db.UpsertAsync("doc-123", myVector, metadata: "{\"title\":\"Hello V2\"}");

// 3. Search (Zero-Allocation SIMD Scan)
float[] query = GetQuery();
var results = await db.SearchAsync(query, new VectorSearchOptions { TopK = 5 });

foreach (var res in results)
{
    Console.WriteLine($"ID: {res.Id}, Score: {res.Score}, Meta: {res.Metadata}");
}
```

---

# SqliteVector.NET (한국어) 🚀

> **SQLite는 진실을, .NET은 검색을.**
> 
> SQLite의 강력한 트랜잭션 관리와 .NET의 MemoryMapped SIMD 스캔을 결합하여, 할당(Allocation) 없이 극한의 성능을 내는 내장형(Embedded) 벡터 검색 엔진입니다.

## 🌟 V2.0 아키텍처의 차별점

기존 방식들은 SQLite 내부에 `BLOB` 형태로 벡터를 저장합니다. 이는 검색 핫패스(Hot path)에서 SQLite 페이지를 순회하고 관리되는 메모리로 복사(`GetBytes`)하는 심각한 병목을 유발합니다.

**SqliteVector.NET v2.0**은 이 역할을 완벽하게 분리했습니다:
- **SQLite**는 ID 매핑, 메타데이터, 트랜잭션(ACID)만 담당합니다.
- **.NET 런타임**은 OS 레벨의 `MemoryMappedFile`을 통해 `.vec` 파일에 직접 접근하여, 복사본 없이 SIMD 연산을 수행합니다.

### ✨ 주요 기능
- 🚀 **Zero-Copy SIMD 검색**: 검색 시 SQLite를 쳐다보지 않습니다. 연속된 메모리를 포인터로 읽어 `AVX2`/`AVX-512` 가속으로 초당 기가바이트(GB/s) 단위의 스캔을 수행합니다.
- 🛡️ **Torn-Write 방어 (Append-Only)**: 모든 벡터는 불변(Immutable) 세그먼트 파일 끝에 CRC32 해시와 함께 추가 기록되어 크래시 발생 시 데이터 오염을 원천 차단합니다.
- 🔄 **스냅샷 격리 (MVCC)**: 검색 중인 Reader와 데이터를 추가하는 Writer가 서로에게 락(Lock)을 걸지 않습니다.
- 💾 **Out-of-Core 스트리밍**: 물리적 RAM 용량을 초과하는 거대 데이터셋을 위해 `ArrayPool`을 활용한 순차 I/O 스트리밍 검색을 지원합니다.
- 🧹 **조각모음 (Compaction)**: 삭제(Tombstone)된 레코드들을 백그라운드에서 정리하고 물리적 용량을 회수하는 가비지 컬렉터가 내장되어 있습니다.

## 🚀 Recent V2.0 Stability Updates
- **G7.1 & G8 (LiveSet Snapshot Isolation)**: Resolved physical stale record visibility bugs. VectorDatabase now dynamically captures an atomic MVCC snapshot (LiveSet BitArray) from SQLite and filters MemoryMappedSearchEngine pointer reads, completely eliminating edge cases with logically deleted or updated vectors.
- **O(K) Zero-Allocation Search**: DenseTopKBuffer has been completely rewritten using value-type Candidate structs. All string allocations during SIMD hot-path have been completely eliminated. Metadata strings are only allocated for the final Top-K results.
- **Crash-safe Reopen and Layout**: Fixed a critical MemoryMappedFile truncation layout bug that triggered AccessViolationException. ActiveSegmentWriter now properly orchestrates struct alignments (128-byte headers, 32-byte entries) and supports resume operations (FileMode.OpenOrCreate) allowing seamless DB restarts.
