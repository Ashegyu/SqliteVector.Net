# SqliteVector.Net V2 — Correctness & Runtime Closure Implementation Instructions

> Target implementer: **Gemini 3.1 Pro**
>
> Project: **SqliteVector.Net**
>
> Goal: Close the remaining correctness, lifetime, durability, recovery, and runtime integration gaps in the current V2 implementation **without unnecessary redesign**.
>
> This document is an implementation directive. Do not treat the current code as correct merely because it compiles or because an existing test passes.

---

# 0. Primary Objective

The current implementation already contains substantial V2 work:

- append-only vector storage
- immutable physical records
- SQLite catalog
- vector record history
- exact SIMD search
- memory-mapped search
- block-based streaming search
- payload CRC
- logical LiveSet filtering
- SearchSnapshot/ref-count foundations
- BenchmarkDotNet benchmarks
- preliminary compaction support

However, the runtime is **not yet production-closed**.

The remaining work is primarily around:

1. real monotonically increasing generation/revision semantics
2. compaction transaction correctness
3. cosine normalization correctness
4. StoreContract atomic persistence and validation
5. Search/Dispose lifetime correctness
6. snapshot consistency
7. recovery validation
8. mmap corruption hardening
9. multi-segment lifecycle
10. deterministic failure/race testing

Do **not** add HNSW, quantization, GPU support, distributed functionality, plugin abstractions, repository layers, factory layers, or unrelated refactoring during this task.

The priority is:

> **Correctness → lifetime → durability → recovery → concurrency → benchmark validation → simplification**

---

# 1. Non-Negotiable Invariants

The final implementation must preserve the following invariants.

## I1 — Immutable committed vector payload

Once a physical vector record becomes committed, its payload must never be modified in place.

Update:

```text
old physical record remains immutable
+
new physical record appended
```

Delete:

```text
logical tombstone only
```

Physical reclamation only occurs through compaction after all readers that can reference the old segment are gone.

---

## I2 — Existing committed version survives failed update

For an update from version A to version B:

```text
failure before B becomes visible
→ A remains valid
```

The system must never enter:

```text
A invalid
AND
B invalid
```

because of a normal interrupted update.

---

## I3 — Physical committed does not mean logically visible

These concepts must remain separate:

```text
PhysicalCommitted
LogicalVisible
```

A physical record may be fully written and CRC-valid but still not be visible because SQLite has not committed the logical mapping yet.

Search visibility must come from the published logical snapshot, not only from physical record flags.

---

## I4 — A search uses one consistent snapshot

A search operation must use one coherent logical snapshot for the entire operation:

```text
Acquire Snapshot R
    ↓
Use LiveSet from R
    ↓
SIMD scan
    ↓
Resolve logical ID / metadata from R-compatible state
    ↓
Release Snapshot R
```

Do not mix:

```text
LiveSet from old revision
+
metadata lookup from latest catalog state
```

---

## I5 — Real monotonic database revision

The current runtime must not use constant `generation = 1`.

Every logical commit that changes visible state must receive a monotonically increasing revision.

Example:

```text
Revision 1  store creation
Revision 2  Upsert A
Revision 3  Upsert B
Revision 4  Delete A
Revision 5  Upsert A
Revision 6  compaction publish
```

At minimum, these must participate in the same revision model:

- SQLite logical mappings
- physical record generation/revision metadata
- SearchSnapshot
- compaction fence
- segment publication

The revision must never move backwards.

---

## I6 — Compaction cannot overwrite a newer user update

If compaction begins from snapshot revision `R`:

```text
Compaction base revision = R
```

and a user writes revision `R+1`, compaction must never move the logical record back to data derived from revision `R`.

A stale compaction publish must fail or skip the changed row.

---

## I7 — Search hot path has no O(N) managed allocation

The scan loop must not allocate per vector.

Forbidden in the per-vector loop:

- `ToString`
- LINQ
- closures
- delegate allocation
- JSON parsing
- SQLite queries
- metadata lookup
- boxing
- per-vector arrays
- per-vector tasks

Target:

```text
managed allocation per scanned vector = 0 B
```

Final result materialization may allocate proportional to `K`.

---

## I8 — Search backend correctness is identical

For the same snapshot/query:

```text
MMap exact search
Streaming exact search
Resident exact search (if present)
```

must return equivalent Top-K results within documented floating-point tolerance.

Storage strategy may change performance, not meaning.

---

## I9 — Dispose cannot invalidate an active operation

Once a search or write operation begins successfully, Dispose must not release resources that operation still owns.

Dispose must:

```text
1. prevent new operations
2. mark database as disposing
3. wait for or coordinate active operations
4. retire snapshots
5. release segment resources
6. close SQLite connections
7. mark disposed
```

No busy loop is allowed.

No search may resolve metadata through a disposed catalog connection.

---

## I10 — Unsafe mmap pointer construction occurs only after validation

Before creating unsafe pointers from a mapped file header, validate every relevant field and every derived range.

A corrupt file must fail with a controlled managed exception.

It must not result in:

- `AccessViolationException`
- memory access outside mapped range
- integer overflow producing a trusted offset
- stack overflow from malicious dimensions

---

# 2. P0 — Implement Real Monotonic Revision/Generation

This is the highest priority.

The current runtime reportedly still writes constant generation values in critical paths. Remove all production hard-coding such as:

```csharp
generation: 1
segmentId: 1
```

where the value is supposed to represent logical revision.

Do not merely modify tests to use higher values.

The runtime itself must generate them.

---

## 2.1 Define one revision source

Use one authoritative source of database revision.

Recommended concept:

```text
DatabaseRevision
```

Persist it in SQLite transactionally.

Possible schema:

```sql
CREATE TABLE database_state
(
    singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
    current_revision INTEGER NOT NULL
);
```

Alternative designs are acceptable if they preserve atomicity and monotonicity.

---

## 2.2 Revision allocation must be transactional

A visible logical change must allocate its revision inside the same SQLite transaction that publishes the logical mapping.

Pseudo-flow:

```text
BEGIN

currentRevision = read current revision
newRevision = currentRevision + 1

update database_state
update logical mapping using newRevision
insert vector_records history as needed

COMMIT
```

Do not:

```text
read revision outside transaction
increment in memory
later commit
```

because multiple operations could duplicate or reorder revisions.

---

## 2.3 Physical append and revision

A physical record may be written before the SQLite revision is committed.

That is acceptable.

The physical record's stored generation/revision semantics must be explicitly defined.

Two valid options:

### Option A

Reserve revision before physical append and publish it later.

### Option B

Treat physical record revision as a physical sequence and logical catalog revision separately.

If these are separate concepts, name them separately.

Do not call two different concepts `generation`.

Preferred clarity:

```text
PhysicalRecordSequence
DatabaseRevision
SegmentGeneration
```

Only introduce separate fields if actually necessary.

Keep the design minimal.

---

## 2.4 Snapshot revision

Every published `SearchSnapshot` must have a real revision:

```csharp
SearchSnapshot.Revision
```

SearchSnapshot revision must correspond to the catalog state used to build its LiveSet/result mapping.

---

# 3. P0 — Fix Compaction Transaction Ownership

The current compaction implementation reportedly creates a transaction using one `SqliteConnection` and assigns it to a command created by another connection.

That is invalid design.

A transaction and every command participating in it must use the same owning connection.

---

## 3.1 Required compaction transaction shape

Use one dedicated connection for the compaction transaction:

```text
CompactionConnection
    ↓
BEGIN TRANSACTION
    ↓
read/validate expected mappings
    ↓
conditional mapping updates
    ↓
vector_records changes
    ↓
segment catalog changes
    ↓
revision update
    ↓
COMMIT
```

Do not mix `_catalog.BeginTransaction()` with `_directConnection.CreateCommand()`.

---

## 3.2 Generation/revision fence

Compaction must start from a known revision `R`.

For each logical record it plans to move, it must only publish the compacted location if the row still matches the expected source state.

A stronger predicate is preferable to:

```sql
generation <= @compactionGen
```

Use optimistic compare-and-swap semantics.

Example:

```sql
UPDATE vectors
SET
    segment_id = @newSegment,
    record_index = @newRecord,
    generation = @newRevision
WHERE
    external_id = @id
    AND segment_id = @expectedOldSegment
    AND record_index = @expectedOldRecord
    AND generation = @expectedOldGeneration
    AND deleted = 0;
```

Then verify:

```text
RowsAffected == 1
```

If zero:

```text
record changed concurrently
→ do not overwrite it
```

This is safer than a broad `generation <= R` predicate.

---

## 3.3 Compaction destination identity

Reject mismatches between:

```text
newSegmentId
```

and:

```text
newHeader.SegmentId
```

Do not allow duplicate independent arguments to silently disagree.

Prefer an object that carries one source of truth.

Example concept:

```csharp
SegmentDescriptor
{
    SegmentId
    Path
    Header
}
```

Do not over-abstract beyond what is needed.

---

# 4. P0 — Fix Cosine Normalization Correctness

If:

```text
Metric = Cosine
NormalizeVectors = true
```

and stored vectors are normalized on write, query vectors must also be normalized once per search.

Current invalid behavior to prevent:

```text
stored vector = [1,0]
query = [10,0]

MMap dot score = 10
Streaming cosine score = 1
```

Both backends must agree.

---

## 4.1 Required search behavior

At search entry:

```text
validate query
↓
if cosine + normalized-store:
    normalize query once
↓
all exact backends use dot product
```

Do not normalize per target vector.

Do not normalize the query in one backend but not another.

---

## 4.2 Zero-vector policy

Define a strict policy.

Recommended:

```text
Cosine + normalized store
→ zero vector is invalid
```

Reject:

- zero vector at insert/upsert
- zero query at search

with a clear exception.

Do not silently treat zero vector as normalized.

---

# 5. P0 — Implement StoreContract as a Real Atomic Contract

The existing `StoreContract` type must become the actual source of truth.

Do not persist contract fields as independent unrelated key-value writes.

---

## 5.1 Required fields

At minimum:

```text
FormatVersion
Dimensions
ElementType
Metric
Normalization
EmbeddingSpaceId
```

If `EmbeddingSpaceId` is optional, explicitly define what `null/empty` means.

---

## 5.2 Preferred schema

Use one singleton row.

Example:

```sql
CREATE TABLE store_contract
(
    singleton INTEGER PRIMARY KEY CHECK(singleton = 1),

    format_version INTEGER NOT NULL,
    dimensions INTEGER NOT NULL,
    element_type INTEGER NOT NULL,
    metric INTEGER NOT NULL,
    normalization INTEGER NOT NULL,
    embedding_space_id TEXT NULL
);
```

Create the whole contract transactionally.

Partial contract state should be impossible.

---

## 5.3 Open behavior

### New store

```text
no contract row
+
no existing initialized vector segments
→ create contract atomically
```

### Existing store

Read the complete persisted contract.

Compare with requested options.

Mismatch must fail before modifying vector files.

Must reject at least:

- dimensions mismatch
- element type mismatch
- metric mismatch
- normalization mismatch
- embedding-space mismatch if both are specified incompatibly
- unsupported format version

---

## 5.4 Never rewrite existing segment headers before compatibility validation

Required reopen flow:

```text
if file does not exist / empty:
    create new validated header
else:
    read existing header
    validate header
    validate against StoreContract
    open
```

Never:

```text
open old file
→ write requested options into header
→ validate later
```

---

# 6. P0 — Add Database Operation Lifetime Gate

Current snapshot ref-counting protects mapped search resources, but the entire database operation must also be protected.

Search can still need:

- snapshot
- catalog result mapping or snapshot metadata
- runtime state
- possibly segment references

Dispose must not invalidate any of these mid-operation.

---

## 6.1 Required state machine

Recommended minimal state:

```text
Running
Disposing
Disposed
```

Operations:

```text
TryEnterOperation()
ExitOperation()
```

Dispose:

```text
transition Running → Disposing atomically
reject new operations
wait for active operations to reach zero
dispose runtime resources
transition Disposing → Disposed
```

Avoid spinning forever.

Use a proper wait primitive / TaskCompletionSource / Semaphore / equivalent.

---

## 6.2 Required tests

Must include deterministic tests for:

```text
Search + Dispose
Upsert + Dispose
Delete + Dispose
RefreshSnapshot + Dispose
```

Search already admitted before Dispose begins must either:

- complete successfully

or

- be cancelled by an explicitly defined cancellation contract

but must not:

- busy-loop
- access disposed SQLite connection
- dereference unmapped memory

---

# 7. P1 — Make SearchSnapshot Truly Coherent

The current LiveSet direction is correct, and the historical `vector_records` table solves an important old-metadata problem.

Keep that.

But define the snapshot semantics explicitly.

---

## 7.1 Snapshot contents

A snapshot must at least identify:

```text
Revision
Segment set
Live physical record set
Result-resolution semantics compatible with that revision
```

If metadata resolution uses `vector_records` history and that history is immutable enough to resolve old physical records, that can be acceptable.

Do not build an enormous immutable metadata dictionary per search unless measurement proves it necessary.

Prefer:

```text
immutable historical record mapping in SQLite
+
database operation lifetime gate
```

if it keeps implementation simpler.

---

## 7.2 Snapshot acquisition

The refcount acquire must be safe against retirement.

Pattern such as `TryAddReference()` is acceptable.

Do not ignore failed reference acquisition.

Do not create a new snapshot using a mapped engine for which reference acquisition failed.

---

## 7.3 Snapshot publication

Writer flow:

```text
physical append complete
↓
SQLite logical commit complete
↓
build new logical LiveSet/state
↓
acquire needed segment references
↓
atomic current-snapshot exchange
↓
retire old snapshot
```

Never publish a snapshot before logical commit.

---

# 8. P1 — Harden MMap Header Validation

Before deriving unsafe pointers, validate the complete file layout.

Use checked arithmetic.

---

## 8.1 Validate header identity

At minimum:

```text
Magic
FormatVersion
ElementType
Dimensions
SegmentId
Capacity
Alignment
PayloadBytes
VectorStride
DirectoryOffset
VectorRegionOffset
```

---

## 8.2 Validate arithmetic relationships

Required invariants:

```text
Dimensions > 0

PayloadBytes == Dimensions * sizeof(float)

Alignment is supported
Alignment is power-of-two if required

VectorStride >= PayloadBytes
VectorStride % Alignment == 0

VectorRegionOffset % Alignment == 0

DirectoryOffset >= HeaderSize

DirectoryEnd =
    DirectoryOffset + Capacity * DirectoryEntrySize

DirectoryEnd <= VectorRegionOffset

VectorRegionEnd =
    VectorRegionOffset + Capacity * VectorStride

VectorRegionEnd <= fileLength
```

All multiplication/addition must be performed in `checked` context or with explicit overflow detection.

---

## 8.3 Do not hardcode alignment validation to 64 if header owns the value

If the file format stores:

```text
Header.Alignment
```

validation must use that value.

If V2 only permits 64, then enforce:

```text
Header.Alignment == 64
```

and document it.

Do not simultaneously pretend alignment is configurable while validating a hardcoded value.

---

# 9. P1 — Header Integrity

Implement the header checksum or explicitly remove the field from the claimed durability contract.

Preferred:

```text
CRC32C over stable header bytes excluding checksum field
```

Validation occurs on reopen before unsafe offsets are trusted.

Do not compute header checksum in the search hot path.

---

# 10. P1 — Physical Record Recovery

Current "find first free record" behavior is not sufficient to call full crash recovery.

Implement explicit recovery/reconciliation.

---

## 10.1 Active segment recovery

Walk physical directory entries in order.

For each candidate committed record validate:

```text
flags
record index
payload bounds
CRC
generation/physical sequence validity
```

Find the valid committed prefix / valid records according to the format.

If the final record is torn/incomplete:

```text
do not expose it
```

If safe to truncate:

```text
truncate to last valid boundary
```

Otherwise quarantine/ignore tail.

---

## 10.2 Catalog ↔ physical reconciliation

At startup verify every current logical catalog mapping points to:

```text
existing segment
existing record
committed physical record
CRC-valid payload
```

If a catalog row references invalid physical data:

```text
store corruption
```

Do not silently guess.

---

## 10.3 Orphan records

A physical record may be committed but not referenced because crash occurred before SQLite logical commit.

This is valid orphan state.

Options:

```text
keep until compaction
or
reclaim during recovery
```

Either is acceptable.

Do not expose orphan physical records in search.

---

# 11. P1 — Search Must Check Both Logical and Physical Visibility

Current search should not rely solely on LiveSet when LiveSet exists.

Required condition:

```text
LiveSet says visible
AND
physical directory says committed
```

Pseudo:

```csharp
if (!liveSet.Get(i))
    continue;

if (entry.Flags != RecordFlags.Committed)
    continue;
```

This gives defense against inconsistent/corrupt state.

---

# 12. P1 — Query Validation

Validate query before entering the hot scan.

Must reject:

```text
wrong dimension
NaN
+Infinity
-Infinity
invalid zero vector for normalized cosine
```

Do this once per query.

Do not perform per-element validation for every candidate vector during search.

---

# 13. P1 — Streaming Search Edge Cases

The current block-based streaming search is a meaningful improvement.

Keep block scanning and `FileOptions.SequentialScan`.

Fix huge-stride safety:

```csharp
vectorsPerBlock = Math.Max(1, blockSize / stride);
```

Also use checked arithmetic.

Ensure:

```text
block boundaries never cause a vector to be interpreted with missing bytes
```

If a block ends mid-vector, carry remainder correctly or choose a block size aligned to stride.

---

# 14. P2 — Multi-Segment Runtime

Current runtime must stop assuming only:

```text
segmentId = 1
segment_000001.vec
capacity = 100000 forever
```

Implement real lifecycle:

```text
Active
→ Sealing
→ Sealed
→ Obsolete
```

When active segment reaches capacity:

```text
seal current
create next segment
publish new snapshot
continue writing
```

SearchSnapshot must contain all searchable segments.

Exact search:

```text
search segment A
search segment B
search segment C
merge Top-K
```

Do not convert this into a large abstraction hierarchy.

---

# 15. P2 — Compaction Runtime Integration

Only after correctness of compaction transaction/fence is fixed.

Required behavior:

```text
select compaction candidates
↓
capture base revision
↓
copy only intended live physical records
↓
write destination segment completely
↓
validate destination
↓
conditional SQLite publish
↓
allocate new database revision
↓
publish new snapshot
↓
retire old segments
↓
wait until no snapshot references remain
↓
delete old files
```

Old segment deletion before reader retirement is forbidden.

---

# 16. P2 — Background Compaction Claims

Until the runtime actually invokes compaction automatically, README must not claim:

```text
Background Compaction: Automatically reclaims physical disk space
```

Either:

1. implement actual runtime scheduling, or
2. downgrade documentation to describe manual/internal compaction capability.

Do not let documentation outrun implementation.

---

# 17. P2 — Streaming Auto-Fallback Claims

If the user must explicitly set:

```csharp
UseStreamingSearch = true
```

then README must not claim seamless automatic out-of-core fallback.

Call it:

```text
Optional streaming exact-search backend
```

until automatic strategy selection exists.

---

# 18. Async API Semantics

Do not expose fake async methods whose entire body is synchronous and ends with:

```csharp
await Task.CompletedTask;
```

or:

```csharp
return await Task.FromResult(result);
```

Choose one:

### Option A — honest synchronous core API

```csharp
Search(...)
Upsert(...)
Delete(...)
```

Add async APIs only where actual asynchronous behavior exists.

### Option B — real serialized writer / async I/O

If maintaining `Async` methods, provide actual asynchronous scheduling/queue/I/O semantics.

Do not use fake async only for naming aesthetics.

---

# 19. Benchmark Correctness

Existing concurrent benchmark patterns may not actually run concurrently if `SearchAsync()` completes synchronously.

Do not benchmark "100 concurrent searches" by calling a synchronous-completing method 100 times and then `Task.WhenAll`.

Use a real concurrency setup.

Possible:

```csharp
Task.Run(() => db.Search(...))
```

for stress benchmarking, or implement genuine async operation scheduling.

---

## 19.1 Benchmark matrix

At minimum:

Dimensions:

```text
64
100
384
768
1536
3072
```

Vector counts:

```text
1K
10K
100K
1M
```

When hardware permits:

```text
corpus < RAM
corpus ≈ RAM
corpus > RAM
```

Measure:

```text
P50
P95
P99
vectors/sec
effective GB/sec
allocation/query
Gen0/1/2
working set
page faults
disk throughput
CPU utilization
```

Separate:

```text
cold-cache
warm-cache
```

Do not compare warm mmap benchmark to cold streaming disk behavior as if equivalent.

---

# 20. Deterministic Concurrency Tests

Do not use timing/sleep-based tests when the invariant depends on a precise interleaving.

Use hooks, barriers, or `ManualResetEventSlim`.

---

## T1 — Search snapshot vs concurrent Upsert

Force:

```text
Search captures Snapshot R
↓ PAUSE
Writer commits Upsert at R+1
↓
Search resumes
```

Expected:

```text
Search completes successfully
Search result is consistent with Snapshot R
Metadata resolve does not fail
```

---

## T2 — Search snapshot vs concurrent Delete

Force:

```text
Search captures Snapshot R
↓ PAUSE
Delete commits R+1
↓
Search resumes
```

Define expected old-snapshot semantics and verify them.

---

## T3 — Compaction vs concurrent Upsert

Force:

```text
Compaction captures A at revision R
↓ PAUSE
User updates A at R+1
↓
Compaction resumes publish
```

Expected:

```text
latest user version remains current
compaction cannot overwrite it
```

---

## T4 — Search vs Dispose

Force:

```text
Search admitted and owns snapshot
↓ PAUSE
Dispose begins
↓
Search resumes
```

Expected:

```text
no deadlock
no busy loop
no disposed SQLite access
no unmapped pointer access
```

---

## T5 — RefreshSnapshot vs Dispose

Force exact race around segment engine reference acquisition.

Expected:

```text
new snapshot never owns already-dead engine
```

---

# 21. Crash / Failure Injection Tests

Current normal-path "atomic update" tests are insufficient.

Create injectable failure points.

Suggested enum:

```csharp
enum FailurePoint
{
    AfterPayloadWrite,
    AfterPayloadFlush,
    AfterDirectoryWrite,
    AfterDirectoryFlush,
    AfterSqliteBegin,
    BeforeSqliteCommit,
    AfterSqliteCommit,
    BeforeSnapshotPublish,
    DuringSegmentSeal,
    DuringCompactionWrite,
    BeforeCompactionCatalogPublish
}
```

Test infrastructure may throw an injected exception.

For process-level durability tests, where feasible, run a child process and kill it.

---

## Crash invariant

At every injected update crash point:

```text
Old version valid
OR
New version valid
```

Never:

```text
both invalid
```

For physical orphan cases:

```text
orphan may exist
but must not be logically visible
```

---

# 22. Required Regression Tests

Add explicit tests for all of these.

### StoreContract

```text
wrong dimensions reopen → fail without modifying files
wrong metric reopen → fail
wrong normalization reopen → fail
wrong element type → fail
embedding-space mismatch → fail
partial contract cannot exist
```

### Input

```text
NaN vector → reject
Infinity vector → reject
NaN query → reject
Infinity query → reject
wrong query dimension → reject
zero cosine vector → according to chosen policy
```

### Top-K

```text
TopK <= 0 → deterministic validation
TopK > N
duplicate scores
all-negative scores
all-equal scores
```

### Backend parity

Use non-unit queries.

Example:

```text
stored A = [1,0]
stored B = [0,1]
query = [10,0]
```

MMap and Streaming scores/results must agree.

### Reopen

```text
create
write
close
reopen
search
same visible results
```

### Corruption

Corrupt:

```text
magic
format version
capacity
alignment
directory offset
vector offset
stride
header checksum
payload CRC
```

Expected:

```text
controlled managed failure
```

never process memory corruption.

---

# 23. Code Review Rules During Implementation

For every change:

1. identify the invariant it protects
2. make the smallest implementation satisfying it
3. add a regression test that fails before the fix
4. run existing tests
5. inspect concurrency/lifetime side effects
6. inspect allocation/performance effects
7. avoid unrelated refactoring

Do not declare success because:

- code compiles
- one happy-path test passes
- benchmark is faster
- README says feature exists

---

# 24. Avoid Premature Optimizations

Do not introduce during closure:

```text
custom AVX intrinsics
AVX-512-only paths
unsafe lock-free queues
NUMA sharding
NativeMemory arena redesign
HNSW
quantization
GPU search
memory pooling everywhere
```

unless an existing benchmark proves a current blocker.

`TensorPrimitives` is acceptable for now if correctness and performance are adequate.

---

# 25. README Accuracy Pass

After implementation, update README to match actual behavior.

Do not claim any of the following unless actually implemented and tested:

```text
Production Ready
Zero Allocation Search
Snapshot Isolation
Crash Safe
Automatic Streaming Fallback
Background Compaction
MVCC
```

Use precise wording.

Examples:

Instead of:

```text
Zero-Allocation Search
```

prefer:

```text
Zero per-vector allocation in the SIMD scan hot path
```

if final-result allocations still exist.

Instead of:

```text
Automatic out-of-core fallback
```

prefer:

```text
Optional sequential streaming exact-search backend
```

unless automatic selection is implemented.

---

# 26. Recommended Implementation Order

Follow this order.

## Phase A — correctness blockers

```text
A1 Real DatabaseRevision
A2 Compaction same-connection transaction
A3 Compaction compare-and-swap fence
A4 Query normalization parity
A5 Atomic StoreContract
A6 Operation lifetime / Dispose gate
```

Do not proceed to multi-segment work until these pass.

---

## Phase B — snapshot/recovery hardening

```text
B1 coherent SearchSnapshot semantics
B2 safe snapshot publication
B3 full mmap header validation
B4 header checksum
B5 active-segment recovery
B6 catalog ↔ physical reconciliation
B7 orphan handling
```

---

## Phase C — segment lifecycle

```text
C1 seal active segment
C2 create next active segment
C3 search multiple segments
C4 snapshot owns segment set
C5 retire old segments safely
```

---

## Phase D — compaction closure

```text
D1 select candidates
D2 copy from captured revision
D3 conditional publish
D4 snapshot swap
D5 retire/delete old segments
D6 deterministic concurrent update tests
```

---

## Phase E — performance closure

```text
E1 MMap vs Streaming parity
E2 realistic concurrency benchmark
E3 cold/warm benchmark separation
E4 allocation verification
E5 P95/P99
E6 simplify any optimization with no measurable gain
```

---

# 27. Definition of Done

Do not mark V2 core complete until all answers below are explicit and backed by tests.

## Correctness

```text
Can an old physical version reappear after Upsert?
→ No

Can a deleted logical vector reappear from historical physical data?
→ No

Can MMap and Streaming return different cosine semantics?
→ No

Can compaction overwrite a newer user update?
→ No
```

---

## Lifetime

```text
Can Dispose invalidate an active search?
→ No

Can snapshot retirement unmap memory still in use?
→ No

Can RefreshSnapshot acquire an already-dead engine?
→ No
```

---

## Durability

```text
Can failed update destroy old committed version?
→ No

Can a torn new record become logically visible?
→ No

Can partial StoreContract initialization be accepted?
→ No
```

---

## Recovery

```text
Can reopen detect invalid header before unsafe pointer usage?
→ Yes

Can catalog mapping to corrupt physical record be detected?
→ Yes

Can physical orphan exist without becoming visible?
→ Yes
```

---

## Performance

```text
Does scan allocation grow linearly with N?
→ No

Does metadata resolution cost scale with N?
→ No

Does streaming operate in blocks rather than one FileStream read per vector?
→ Yes

Are concurrency benchmarks actually concurrent?
→ Yes
```

---

# 28. Required Final Report from Gemini 3.1 Pro

When implementation is complete, produce a final report containing:

## 1. Changed files

For every changed file:

```text
file
reason
protected invariant
```

---

## 2. Behavior changes

List exact externally observable changes.

Examples:

```text
NormalizeVectors=true now normalizes query
wrong normalization contract now rejects reopen
Dispose now waits for admitted operations
```

---

## 3. Tests added

List every new test and the failure mode it covers.

---

## 4. Test results

Report actual executed commands and results.

Example:

```text
dotnet build -c Release
Passed

dotnet test -c Release
123 passed / 0 failed
```

Do not report tests as passed if they were not run.

---

## 5. Benchmarks

Report:

```text
hardware
runtime
dataset dimensions
vector count
backend
warm/cold
mean
P95 if available
allocation
```

---

## 6. Remaining known risks

Do not say "none" unless justified.

Especially mention anything still not tested:

```text
real power-loss durability
network filesystem
multi-process access
very large corpus
very large dimensions
Windows/Linux differences
```

---

# 29. Final Review Checklist

Before declaring completion, explicitly answer:

- What is the weakest remaining correctness point?
- Is every "generation" field using the intended semantic?
- Is there exactly one authoritative source of logical revision?
- Can stale compaction overwrite a newer user update?
- Can any active operation touch disposed resources?
- Can any corrupt header value reach unsafe pointer arithmetic before validation?
- Does normalized cosine produce backend-identical scores?
- Can an incomplete contract be represented?
- Does reopening modify files before compatibility validation?
- Does the current code truly support multiple segments?
- Is background compaction actually connected to runtime?
- Is every README claim backed by implementation and tests?
- Which complexity added during this work could be removed without weakening invariants?

If any answer is unclear, the work is not complete.

---

# 30. Final Guiding Principle

Do not optimize the code into complexity before the runtime semantics are closed.

The target is not the most sophisticated vector database.

The target is:

> **A small .NET-native embedded vector engine whose visible state is predictable, whose ownership/lifetime is explicit, whose failures preserve committed data, and whose performance can be measured honestly.**

First design is only a candidate.

First implementation is only the beginning of verification.
