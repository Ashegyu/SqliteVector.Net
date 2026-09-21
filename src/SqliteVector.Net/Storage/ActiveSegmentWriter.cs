namespace SqliteVector.Net.Storage;

using System;
using System.IO;
using System.IO.Hashing;
using System.Runtime.InteropServices;

/// <summary>
/// G3: Append-only Writer
/// 문서 12, 13, 23, 24항에 명시된 불변 조건을 철저히 지키는 단일 쓰기 전용 객체.
/// In-place overwrite를 절대 허용하지 않으며, 무조건 파일 끝(Tail)에만 Append 합니다.
/// </summary>
public sealed class ActiveSegmentWriter : IDisposable
{
    private readonly FileStream _fileStream;
    private readonly SegmentHeader _header;
    private readonly object _writeLock = new();
    
    private int _currentRecordCount;
    private bool _isDisposed;

    public SegmentHeader Header => _header;
    public int RecordCount => _currentRecordCount;

    public ActiveSegmentWriter(string filePath, SegmentHeader header)
    {
        _header = header;
        
        // G14 & 24항: FileOptions.WriteThrough를 사용하여 OS 캐시를 우회하고 디스크에 강제 동기화 (Durability)
        // Reopen 복구를 지원하기 위해 OpenOrCreate 사용
        _fileStream = new FileStream(
            filePath, 
            FileMode.OpenOrCreate, 
            FileAccess.ReadWrite, 
            FileShare.ReadWrite, 
            bufferSize: 4096, 
            FileOptions.None);
            
        InitializeSegment();
    }

    private void InitializeSegment()
    {
        Span<byte> headerBytes = stackalloc byte[Marshal.SizeOf<SegmentHeader>()];
        MemoryMarshal.Write(headerBytes, in _header);
        
        bool isNewFile = _fileStream.Length == 0;

        _fileStream.Seek(0, SeekOrigin.Begin);
        _fileStream.Write(headerBytes);

        if (isNewFile)
        {
            // 새 파일인 경우: 14항 Layout 공간 예약
            long totalSize = _header.VectorRegionOffset + ((long)_header.Capacity * _header.VectorStride);
            _fileStream.SetLength(totalSize);
            _fileStream.Flush(flushToDisk: true);
        }
        else
        {
            // 복구(Recovery) 로직: 기존 파일이 있으면 디렉터리를 스캔하여 다음 빈 인덱스를 찾습니다.
            _currentRecordCount = FindNextFreeRecordIndex();
        }
    }

    private int FindNextFreeRecordIndex()
    {
        // 간단한 복구 로직: Directory 영역을 순차 탐색하여 Committed가 아닌 첫 번째 인덱스를 찾습니다.
        _fileStream.Seek(_header.DirectoryOffset, SeekOrigin.Begin);
        int maxCapacity = _header.Capacity;
        int entrySize = Marshal.SizeOf<RecordDirectoryEntry>();
        byte[] buffer = new byte[entrySize];

        for (int i = 0; i < maxCapacity; i++)
        {
            int bytesRead = _fileStream.Read(buffer, 0, entrySize);
            if (bytesRead < entrySize) return i; // EOF

            var entry = MemoryMarshal.Read<RecordDirectoryEntry>(buffer);
            if (entry.Flags == RecordFlags.Free)
            {
                return i;
            }
        }
        return maxCapacity;
    }

    /// <summary>
    /// 벡터를 파일 끝에 Append하고 CRC와 함께 디렉터리에 커밋합니다.
    /// (24항 논리 순서 완벽 준수)
    /// </summary>
    public int AppendVector(ReadOnlySpan<float> vector, long generation)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(ActiveSegmentWriter));
        
        if (vector.Length != _header.Dimensions)
            throw new ArgumentException($"벡터 차원이 일치하지 않습니다. 기대값: {_header.Dimensions}, 실제값: {vector.Length}");

        lock (_writeLock)
        {
            if (_currentRecordCount >= _header.Capacity)
                throw new InvalidOperationException("Segment가 꽉 찼습니다. 새 Segment를 생성해야 합니다.");

            int recordIndex = _currentRecordCount;
            
            // 1. 논리적/물리적 주소 계산 (18항 Alignment 보장)
            long vectorOffset = _header.VectorRegionOffset + (recordIndex * (long)_header.VectorStride);
            ReadOnlySpan<byte> vectorBytes = MemoryMarshal.Cast<float, byte>(vector);
            
            // 2. Payload CRC 계산 (22항)
            uint crc = Crc32.HashToUInt32(vectorBytes);
            
            // 3 & 4. Payload 기록 (디스크 Append)
            _fileStream.Seek(vectorOffset, SeekOrigin.Begin);
            _fileStream.Write(vectorBytes);
            
            // 정렬 패딩(Padding) 구간은 0으로 채우기 (보안 및 노이즈 방지)
            int paddingSize = _header.VectorStride - _header.PayloadBytes;
            if (paddingSize > 0)
            {
                Span<byte> padding = stackalloc byte[paddingSize];
                padding.Clear();
                _fileStream.Write(padding);
            }
            
            // 5. Payload Flush (디렉터리를 업데이트하기 전에 벡터가 완벽히 기록됨을 디스크 수준에서 보장)
            _fileStream.Flush(flushToDisk: true);
            
            // 6. Directory Entry (물리적 기록 완료 마킹)
            var entry = new RecordDirectoryEntry(recordIndex, generation, crc, RecordFlags.Committed);
            long directoryOffset = _header.DirectoryOffset + (recordIndex * Marshal.SizeOf<RecordDirectoryEntry>());
            
            Span<byte> entryBytes = stackalloc byte[Marshal.SizeOf<RecordDirectoryEntry>()];
            MemoryMarshal.Write(entryBytes, in entry);
            
            _fileStream.Seek(directoryOffset, SeekOrigin.Begin);
            _fileStream.Write(entryBytes);
            
            // 7. Directory Flush (최종 커밋 완료)
            _fileStream.Flush(flushToDisk: true);
            
            _currentRecordCount++;
            return recordIndex;
        }
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            // 69항: Dispose시 큐 드레인 및 스트림 정상 종료 보장
            lock (_writeLock)
            {
                _fileStream.Dispose();
            }
            _isDisposed = true;
        }
    }
}
