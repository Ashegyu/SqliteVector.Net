namespace SqliteVector.Net;

/// <summary>
/// 검색 결과 데이터 트랜스퍼 객체
/// </summary>
public readonly record struct VectorSearchResult(
    string Id, 
    float Score, 
    string? Metadata
);
