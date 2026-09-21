namespace SqliteVector.Net.Storage;

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.IO.Hashing;

/// <summary>
/// G4: Segment Reader
/// 기록된 .vec 파일의 Header와 Directory를 검증하고 벡터 데이터를 안전하게 제공합니다.
/// (초기 버전: FileStream 기반. 향후 G9, G10에서 MMap 및 Streaming 최적화로 확장됨)
/// </summary>
public sealed class SegmentReader : IDisposable
{
    private readonly FileStream _fs;
    private readonly SegmentHeader _header;
    private readonly RecordDirectoryEntry[] _directory;

    public SegmentHeader Header => _header;
    public ReadOnlySpan<RecordDirectoryEntry> Directory => _directory;

    public SegmentReader(string filePath)
    {
        _fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        // 1. Header 읽기 및 검증
        Span<byte> headerBytes = stackalloc byte[Marshal.SizeOf<SegmentHeader>()];
        _fs.ReadExactly(headerBytes);
        _header = MemoryMarshal.Read<SegmentHeader>(headerBytes);

        if (_header.Magic != SegmentHeader.MagicNumber)
            throw new InvalidDataException("유효하지 않은 Vector Segment 파일입니다. (Magic Number 불일치)");

        // 2. Directory 읽기 (메모리에 캐싱하여 검색 시 I/O 방지)
        _fs.Seek(_header.DirectoryOffset, SeekOrigin.Begin);
        int entrySize = Marshal.SizeOf<RecordDirectoryEntry>();
        
        // 주의: Header의 RecordCount는 쓰기 진행 중일 땐 Capacity보다 작을 수 있음.
        // Reader는 파일 생성 당시의 Capacity만큼 예약된 디렉터리를 읽되, 유효한(Committed) 것만 취급함.
        _directory = new RecordDirectoryEntry[_header.Capacity];
        
        // 성능을 위해 디렉터리 전체를 한 번에 읽음
        int dirTotalBytes = _header.Capacity * entrySize;
        byte[] dirBuffer = new byte[dirTotalBytes];
        
        // 파일을 끝까지 다 쓰지 않은 상태(Active)일 수 있으므로 읽을 수 있는 만큼만 읽음
        int bytesRead = _fs.Read(dirBuffer, 0, dirTotalBytes);
        int recordsToRead = bytesRead / entrySize;

        for (int i = 0; i < recordsToRead; i++)
        {
            ReadOnlySpan<byte> slice = new ReadOnlySpan<byte>(dirBuffer, i * entrySize, entrySize);
            _directory[i] = MemoryMarshal.Read<RecordDirectoryEntry>(slice);
        }
    }

    /// <summary>
    /// 특정 레코드 인덱스의 벡터 데이터를 읽어 대상(Span)에 복사합니다.
    /// (검색 Hot Path에서는 할당(Allocation)이 발생하지 않도록 Span 기반 API 사용)
    /// </summary>
    public void ReadVector(int recordIndex, Span<float> destination)
    {
        if (recordIndex < 0 || recordIndex >= _directory.Length)
            throw new ArgumentOutOfRangeException(nameof(recordIndex));

        if (destination.Length != _header.Dimensions)
            throw new ArgumentException("Destination span size must match vector dimensions.");

        var entry = _directory[recordIndex];
        if (entry.Flags != RecordFlags.Committed)
            throw new InvalidOperationException("Committed 상태의 벡터만 읽을 수 있습니다.");

        long offset = _header.VectorRegionOffset + (recordIndex * (long)_header.VectorStride);
        _fs.Seek(offset, SeekOrigin.Begin);

        // Payload 읽기
        Span<byte> payloadBuffer = stackalloc byte[_header.PayloadBytes];
        _fs.ReadExactly(payloadBuffer);

        // 62항. Active Tail Recovery (Torn Write 방어 읽기 검증)
        uint crc = Crc32.HashToUInt32(payloadBuffer);
        if (crc != entry.PayloadCRC)
            throw new InvalidDataException($"Record {recordIndex}의 Payload CRC가 불일치합니다. (Torn Write 의심)");

        // 바이트 배열을 float Span으로 캐스팅 후 복사 (Zero-Allocation)
        MemoryMarshal.Cast<byte, float>(payloadBuffer).CopyTo(destination);
    }

    public void Dispose()
    {
        _fs.Dispose();
    }
}
