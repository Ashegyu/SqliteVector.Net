<div align="center">
  
# 🚀 SqliteVector.NET

**Zero-dependency, Pure C# SIMD Vector Search built on SQLite.**

[![NuGet Version](https://img.shields.io/nuget/v/SqliteVector.NET.svg?style=flat-square)](https://www.nuget.org/packages/SqliteVector.NET/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg?style=flat-square)](LICENSE)

</div>

## 왜 SqliteVector.NET 인가요?

AI 시대를 맞아 로컬 벡터 데이터베이스의 수요가 폭발적으로 증가하고 있습니다. 하지만 기존 솔루션(`sqlite-vec` 등)은 C/C++ 네이티브 확장 플러그인을 요구하여 **Windows/Linux/Mac 크로스 플랫폼 배포 시 네이티브 의존성 문제(DllNotFoundException 등)**를 일으키곤 합니다.

**SqliteVector.NET**은 이 문제를 완벽히 해결합니다. 
순수 `.NET 9` 기능과 기본 `Microsoft.Data.Sqlite`만을 사용하여 **100% Managed C# 코드**로 빛의 속도에 가까운 벡터 검색을 구현했습니다.

### ✨ 주요 특징 (Features)

- **Zero Native Dependencies**: C/C++ 플러그인 컴파일러나 외부 바이너리가 전혀 필요 없습니다. `.NET`이 도는 곳이면 어디서든 동작합니다.
- **Blazing Fast (SIMD Accelerated)**: C# `System.Numerics.Tensors.TensorPrimitives`를 활용하여 AVX2/AVX-512 하드웨어 가속을 통해 수십만 개의 벡터를 1초 이내에 검색합니다.
- **Zero-Allocation Search Loop**: 검색 루프 내에서 가비지 컬렉터(GC) 할당을 0으로 만들어 메모리 스파이크를 방지합니다.
- **Metadata Support**: 벡터뿐만 아니라 원본 텍스트(Payload/Metadata)를 함께 저장하고 즉시 꺼내 쓸 수 있습니다.
- **Any AI Model**: 생성자에서 차원(Dimensions)을 지정하여 OpenAI(1536), HuggingFace E5(384) 등 모든 임베딩 모델과 완벽하게 호환됩니다.

---

## ⚡ 벤치마크 (Benchmarks)

*환경: Windows x64, .NET 10.0*

| 작업 | 데이터 건수 | 차원 수 (AI 모델) | 소요 시간 | GC 할당 |
|:---|---:|---:|---:|---:|
| **검색 (Search)** | 50,000 건 | 1536 (OpenAI) | **~210ms** | **0 Bytes** |
| **검색 (Search)** | 100,000 건 | 384 (HuggingFace) | **~290ms** | **0 Bytes** |

---

## 📦 설치 방법 (Installation)

```bash
dotnet add package SqliteVector.NET
```

---

## 🚀 빠른 시작 (Quick Start)

사용법은 극단적으로 간단합니다.

```csharp
using SqliteVector.NET;

// 1. 초기화: SQLite DB 파일 경로와 사용할 모델의 차원 수 입력
// (예: OpenAI text-embedding-3-small 은 1536차원)
using var store = new SqliteVectorStore("my_rag_db.sqlite", dimensions: 1536);

// 2. 데이터 저장 (Upsert)
// AI 모델을 통해 변환한 float[] 벡터 배열을 준비합니다.
float[] myVector = GetEmbeddingFromAI("사과는 맛있고 건강에 좋습니다.");

// 텍스트 메타데이터를 함께 저장할 수 있습니다.
await store.UpsertAsync(
    id: "doc-001", 
    vector: myVector, 
    metadata: "사과는 맛있고 건강에 좋습니다."
);

// 3. 데이터 검색 (Search)
float[] queryVector = GetEmbeddingFromAI("건강한 과일 정보 알려줘");

var results = await store.SearchAsync(queryVector, topK: 3);

foreach (var result in results)
{
    Console.WriteLine($"ID: {result.Id}");
    Console.WriteLine($"Score: {result.Score}");
    Console.WriteLine($"원본 텍스트: {result.Metadata}");
}
```

## 🧠 활용 사례 (Use Cases)

- **로컬 RAG (Retrieval-Augmented Generation)** 애플리케이션
- 프라이버시가 중요한 데스크톱/모바일 앱의 온디바이스 시맨틱 검색
- Pinecone, ChromaDB 등 무거운 외부 인프라 구축이 부담스러운 소규모 프로젝트
- MAUI, WPF, Windows Forms 앱 내장 지식베이스

---
*Built with ❤️ for the .NET Community.*
