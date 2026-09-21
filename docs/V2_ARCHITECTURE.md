# SqliteVector.Net 2.0 Architecture Design

## 1. 목표

`SqliteVector.Net 2.0`의 목표는 SQLite에 벡터 검색 기능을 억지로 집어넣는 것이 아니다.

핵심 목표는 다음이다.

> **SQLite의 안정적인 영속성과 .NET의 메모리·SIMD 실행 성능을 결합한 .NET-first embedded vector search engine**

즉 역할을 명확하게 나눈다.

```text
SQLite
= metadata / identity / transaction / recovery / catalog

.NET Vector Engine
= vector storage / scan / distance / Top-K / indexing
```

기존 `SqliteVector.Net 1.x`의 가장 큰 문제는 이 두 역할이 한 경로에 섞여 있다는 것이다.

```text
SQLite SELECT
    ↓
BLOB materialization
    ↓
C# buffer
    ↓
SIMD
```

2.0에서는 검색 hot path에서 SQLite를 제거하는 것을 핵심 설계 원칙으로 한다.

---
*(이하 전체 아키텍처 원문은 동일하게 유지되며, 향후 개발의 나침반으로 사용됩니다.)*
