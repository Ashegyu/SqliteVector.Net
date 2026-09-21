namespace SqliteVector.Net.Storage;

using System;
using System.Runtime.InteropServices;
using SqliteVector.Net.Catalog;

/// <summary>
/// G2: Vector Segment Header (고정 128 바이트)
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 128)]
public struct SegmentHeader
{
    // "SVN2" (SqliteVector.Net V2) in ASCII Little Endian
    public const uint MagicNumber = 0x324E5653; 

    public readonly uint Magic;
    public readonly int FormatVersion;
    public readonly long SegmentId;
    public readonly long Generation;
    
    public readonly int Dimensions;
    public readonly VectorElementType ElementType;
    
    /// <summary>
    /// 순수 데이터 크기 (예: 1536차원 * 4바이트 = 6144)
    /// </summary>
    public readonly int PayloadBytes;
    
    /// <summary>
    /// SIMD 정렬(Alignment) 패딩이 포함된 실제 점유 크기
    /// (예: 차원이 100(400B)이고 Alignment가 64면 VectorStride는 448)
    /// </summary>
    public readonly int VectorStride;
    
    public readonly int Alignment;
    
    public readonly int Capacity; // Segment가 가질 수 있는 최대 레코드 수
    public readonly long DirectoryOffset;
    public readonly long VectorRegionOffset;
    
    public readonly uint HeaderChecksum;

    public SegmentHeader(
        long segmentId, long generation, int dimensions, 
        VectorElementType elementType, int capacity, int alignment)
    {
        Magic = MagicNumber;
        FormatVersion = 2;
        SegmentId = segmentId;
        Generation = generation;
        Dimensions = dimensions;
        ElementType = elementType;
        
        PayloadBytes = elementType == VectorElementType.Float32 ? dimensions * 4 : dimensions;
        Alignment = alignment;
        
        // 17항. Alignment 계산 (SIMD 정렬 보장)
        VectorStride = AlignUp(PayloadBytes, alignment);
        
        Capacity = capacity;
        
        // Header 크기는 Struct Layout에 맞춰 128바이트 고정입니다.
        long headerSize = 128; 
        DirectoryOffset = headerSize;
        
        // Directory 크기 계산 (RecordDirectoryEntry 크기 32바이트 고정)
        long minVectorOffset = DirectoryOffset + (capacity * 32L);
        
        // 18항. Vector Region 시작 위치도 Alignment에 맞춰서 시작해야 함
        VectorRegionOffset = AlignUpLong(minVectorOffset, alignment);
        
        HeaderChecksum = 0; // TODO: V2.0 CRC32C 적용 시 구현
    }

    /// <summary>
    /// 비트 연산을 사용한 초고속 O(1) Padding 크기 계산 (2의 제곱수만 허용)
    /// </summary>
    public static int AlignUp(int value, int alignment)
    {
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentException("Alignment는 2의 제곱수(32, 64 등)여야 합니다.");
        return (value + alignment - 1) & ~(alignment - 1);
    }

    public static long AlignUpLong(long value, int alignment)
    {
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentException("Alignment는 2의 제곱수(32, 64 등)여야 합니다.");
        return (value + alignment - 1) & ~(alignment - 1L);
    }
}
