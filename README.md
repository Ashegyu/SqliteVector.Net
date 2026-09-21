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

## ⚖️ Disclaimer of Liability

**This software is provided "as is", without warranty of any kind, express or implied.** 
By using `SqliteVector.NET`, you agree that the authors, contributors, or copyright holders shall not be liable for any claim, damages, data loss, or other liability, whether in an action of contract, tort, or otherwise, arising from, out of, or in connection with the software or the use or other dealings in the software. Please thoroughly test this software in your own environment before deploying it to production.

---

# SqliteVector.NET (한국어) 🚀

> **SQLite는 진실을, .NET은 검색을 담당합니다.**
> 
> SQLite의 트랜잭션 메타데이터 관리 능력과 .NET의 충돌 복구(Crash-safe)가 가능한 불변(Immutable) 벡터 세그먼트, 그리고 Zero-Allocation SIMD 검색을 결합한 .NET 네이티브 임베디드 벡터 검색 엔진입니다.

## 💡 왜 SqliteVector.NET v2.0 인가요?

기존의 접근 방식은 벡터 부동소수점 데이터를 SQLite 내부의 `BLOB`으로 저장합니다. 이는 검색 핫 패스(Hot path)에서 SQLite 페이지 순회 및 관리되는 메모리 할당(`GetBytes`)으로 인해 심각한 병목 현상을 유발합니다.

**SqliteVector.NET v2.0**은 아키텍처를 완벽하게 분리했습니다:
- **SQLite 카탈로그**: 식별자(ExternalId), 메타데이터, 논리적 매핑 및 ACID 트랜잭션을 처리합니다.
- **.NET 벡터 엔진**: OS 수준의 `MemoryMappedFile`과 SIMD(`TensorPrimitives`)를 활용하여 초당 기가바이트(GB/s) 수준의 정밀 스캔을 수행하며, 추가만 가능한(Append-Only) `.vec` 파일을 통해 원시 스토리지를 관리합니다.

### 🛡️ 프로덕션 레벨의 불변성 및 보장 (Invariants & Guarantees)

SqliteVector.NET v2.0은 극한의 적대적 조건(Adversarial conditions), 스케일 고갈, 그리고 하드웨어 장애에 대비하여 견고하게 설계되었습니다:

- 🏎️ **Zero-Copy SIMD 검색**: 핫 패스에서 SQLite를 완전히 우회합니다. 직접적인 메모리 포인터(`unsafe` span)를 사용하여 연속적인 벡터 파일을 스캔합니다.
- 🧱 **충돌에 안전한 Append-Only 스토리지**: 벡터는 정확한 CRC32 체크섬과 함께 불변의 물리적 세그먼트에 추가됩니다. 디스크 기록 중 하드 크래시가 발생하더라도 부분 페이로드(Torn-write)는 복구 시 깔끔하게 무시됩니다.
- 🔄 **스냅샷 격리 (MVCC)**: 동시 다발적인 Reader와 백그라운드 Writer는 **절대** 서로를 차단(Block)하지 않습니다. 전용 커넥션 풀링을 통해 동시성이 높은 교차 세그먼트 쿼리 중에도 `ArgumentOutOfRangeException`이나 상태 누수가 발생하지 않음을 보장합니다.
- 📉 **스케일 및 자원 고갈 방어 (Resource Exhaustion Defense)**:
  - 디스크 풀(`ENOSPC`) 상황에서의 우아한 저하(Graceful degradation): 실패한 페이로드나 트랜잭션은 원자적으로 롤백되어 물리적/논리적 데이터베이스를 완벽하게 건강한 상태로 유지합니다.
  - 완벽하게 제한된 메모리 바운드와 `SemaphoreSlim` 백프레셔(Backpressure)를 통해 10,000건의 동시 쓰기 폭풍(Write-storms)을 우아하게 처리합니다.
  - 검색 핫 패스에서 취소 폭풍(`CancellationToken`)을 견뎌내며, `MemoryMapped` 참조 카운트를 누수시키거나 파일을 잠그지 않습니다.
- 🧹 **원자적 컴팩션 (Atomic Compaction)**: 백그라운드 엔진이 논리적으로 덮어씌워진(Overwritten) 벡터들로부터 물리적 디스크 공간을 회수합니다. 컴팩션 프로세스는 원자적인 `.tmp` 파일 이름 변경을 사용하므로, 중간에 크래시가 발생하더라도 반쪽짜리 활성 세그먼트를 남기지 않고 안전하게 중단됩니다.
- 🛡️ **손상 방어 (Unsafe Pointer Guard)**: `mmap` 뷰가 설정되기 전에 세그먼트 헤더에 대해 엄격한 수학적 경계 검사가 수행됩니다. 파일 절단, 헤더 오염, 또는 잘못된 페이로드 오프셋은 `VectorStoreCorruptionException`과 함께 안전하게 거부되어 프로세스가 사망(`AccessViolationException`)하는 것을 방지합니다.

## 🚀 빠른 시작 (Quick Start)

```csharp
using SqliteVector.Net;
using SqliteVector.Net.Catalog;

// 1. 데이터베이스 초기화
var options = new VectorDatabaseOptions 
{ 
    Dimensions = 1536, // 예: OpenAI text-embedding-3-small
    Metric = VectorMetric.Cosine, // Cosine, EuclideanSquared, InnerProduct 지원
    SegmentCapacity = 100_000 // .vec 파일당 물리적 벡터 수
};

// SQLite 카탈로그와 MemoryMapped 벡터 세그먼트를 안전하게 엽니다.
await using var db = await VectorDatabase.OpenAsync("./knowledge_dir", options);

// 2. 벡터 삽입 및 업데이트 (Atomic)
float[] myVector = GetEmbedding();
await db.UpsertAsync("doc-123", myVector, metadata: "{\"title\":\"Hello V2\"}");

// 3. 검색 (Zero-Allocation SIMD 스캔)
float[] query = GetQuery();
var results = await db.SearchAsync(query, new VectorSearchOptions { TopK = 5 });

foreach (var res in results)
{
    Console.WriteLine($"ID: {res.Id}, 점수: {res.Score}, 메타데이터: {res.Metadata}");
}

// 4. 백그라운드 컴팩션 (Compaction)
// Sealed 세그먼트에서 덮어씌워진 벡터들을 정리하고 디스크 공간을 회수합니다.
await db.CompactAsync();
```

## 🏗️ 아키텍처 파이프라인 (Under the Hood)

### 1. Catalog Phase
`UpsertAsync`가 호출되면 벡터는 `WriteThrough`를 사용하는 OS FileStream 기반의 `ActiveSegmentWriter`에 직접 직렬화됩니다. 디스크에 물리적으로 플러시되면, SQLite 카탈로그는 CAS(Compare-And-Swap) 트랜잭션을 원자적으로 실행하여 `ExternalId`를 새로 기록된 물리적 포인터(Segment ID + Record Index)에 바인딩합니다.

### 2. LiveSet Snapshotting
`SearchAsync`가 시작되면, VectorDatabase는 물리적 레코드의 활성 상태를 나타내는 `BitArray` (LiveSet)와 함께 모든 물리적 세그먼트 포인터의 Lock-free 스냅샷을 캡처합니다.

### 3. Execution Phase
검색 요청은 모든 `MemoryMappedSearchEngine` 인스턴스로 분산(Fan-out)됩니다. `unsafe` 포인터가 물리적 벡터 부동소수점을 순회하며, 스냅샷된 `LiveSet`을 사용하여 삭제된 요소를 마스킹합니다. 결과는 문자열 할당(String allocation)이 전혀 없는 스레드 안전한 `DenseTopKBuffer`에 유지됩니다. 최종적인 글로벌 Top-K 식별자들만 SQLite에서 지연(Lazy) 해석됩니다.

## ⚖️ 책임 면제 조항 (Disclaimer of Liability)

**본 소프트웨어는 상품성이나 특정 목적에 대한 적합성을 포함하여, 어떠한 명시적이나 묵시적인 보증 없이 "있는 그대로(As is)" 제공됩니다.**
`SqliteVector.NET`을 사용함에 있어 발생하는 데이터 손실, 서비스 중단, 버그로 인한 금전적 손해 등 어떠한 경우에도 소프트웨어의 작성자, 기여자 또는 저작권자는 계약, 불법 행위 등에 관계없이 어떠한 책임도 지지 않습니다. 상용 환경(Production)에 도입하기 전에 반드시 귀하의 환경에서 충분한 자체 테스트를 거치시기 바랍니다.
