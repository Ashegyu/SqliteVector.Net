# SqliteVector.Net 2.0 Architecture Design 0.2

## 0. 문서 상태

**Status:** Architecture Candidate
**Version:** 0.2
**Target:** SqliteVector.Net 2.x
**Primary Runtime:** .NET 10+
**Primary Language:** C#

이 문서는 구현 확정안이 아니다.

특히 다음 항목은 반드시 benchmark와 crash test를 통과한 뒤 확정한다.

* MemoryMappedFile vs Sequential Streaming
* 32/64-byte vector alignment
* Segment size
* Search parallelism
* CRC32C 비용
* Quantization 도입 시점
* ANN 도입 기준

---
*(이하 전체 0.2 아키텍처 원문을 동일하게 유지하며, 향후 마이그레이션의 최종 나침반으로 사용됩니다.)*
