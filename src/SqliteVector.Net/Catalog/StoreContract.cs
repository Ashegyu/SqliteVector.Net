namespace SqliteVector.Net.Catalog;

using System;

public enum VectorElementType : byte 
{ 
    Float32 = 1, 
    Int8 = 2 // V2.1 Quantization 예약
}

public enum VectorMetric : byte 
{ 
    DotProduct = 1, 
    Cosine = 2, 
    EuclideanSquared = 3 
}

public enum VectorNormalization : byte 
{ 
    None = 0, 
    UnitLength = 1 
}

/// <summary>
/// G1: Store Contract
/// 저장소 생성 시 최초 1회 기록되며 이후 절대 변경 불가능한 불변 조건(Invariant)입니다.
/// 서로 다른 차원(Dimension)이나 임베딩 공간(Embedding Space)의 데이터가 섞이는 것을 원천 차단합니다.
/// </summary>
public sealed record StoreContract
{
    public int FormatVersion { get; init; } = 2; // V2 Architecture
    public int Dimensions { get; init; }
    public VectorElementType ElementType { get; init; } = VectorElementType.Float32;
    public VectorMetric Metric { get; init; }
    public VectorNormalization Normalization { get; init; }
    
    /// <summary>
    /// 예: "openai:text-embedding-3-small:v1"
    /// 차원 수가 같아도 모델이 다르면 거부하기 위한 식별자
    /// </summary>
    public string EmbeddingSpaceId { get; init; } = string.Empty;

    public void Validate()
    {
        if (Dimensions <= 0) 
            throw new ArgumentOutOfRangeException(nameof(Dimensions), "차원 수는 0보다 커야 합니다.");
            
        if (string.IsNullOrWhiteSpace(EmbeddingSpaceId))
            throw new ArgumentException("EmbeddingSpaceId는 반드시 지정되어야 합니다.", nameof(EmbeddingSpaceId));
    }
}
