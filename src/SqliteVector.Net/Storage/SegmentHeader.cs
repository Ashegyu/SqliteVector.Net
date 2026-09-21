namespace SqliteVector.Net.Storage;

using System;
using System.Runtime.InteropServices;
using SqliteVector.Net.Catalog;

/// <summary>
/// G2: Vector Segment Header (고정 128 바이트)
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
public struct SegmentHeader
{
    // "SVN2" (SqliteVector.Net V2) in ASCII Little Endian
    public const uint MagicNumber = 0x324E5653; 

    [FieldOffset(0)]  public readonly uint Magic;
    [FieldOffset(4)]  public readonly int FormatVersion;
    [FieldOffset(8)]  public readonly long SegmentId;
    [FieldOffset(16)] public readonly long Generation;
    
    [FieldOffset(24)] public readonly int Dimensions;
    [FieldOffset(28)] public readonly VectorElementType ElementType;
    
    [FieldOffset(32)] public readonly int PayloadBytes;
    
    [FieldOffset(36)] public readonly int VectorStride;
    
    [FieldOffset(40)] public readonly int Alignment;
    
    [FieldOffset(44)] public readonly int Capacity; // Segment가 가질 수 있는 최대 레코드 수
    [FieldOffset(48)] public readonly long DirectoryOffset;
    [FieldOffset(56)] public readonly long VectorRegionOffset;
    
    [FieldOffset(124)] public readonly uint HeaderChecksum;

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
        
        HeaderChecksum = 0;
    }

    public SegmentHeader WithChecksum()
    {
        SegmentHeader newHeader = this;
        unsafe
        {
            uint* ptr = (uint*)&newHeader;
            *(ptr + 31) = 0; // Clear checksum field (assuming it is the last 4 bytes of 128)
            ReadOnlySpan<byte> bytes = new ReadOnlySpan<byte>(ptr, 124);
            *(ptr + 31) = System.IO.Hashing.Crc32.HashToUInt32(bytes);
        }
        return newHeader;
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
