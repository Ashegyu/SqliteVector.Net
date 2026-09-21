namespace SqliteVector.Net.Storage;

using System.Runtime.InteropServices;

[Flags]
public enum RecordFlags : byte
{
    Free = 0,
    Committed = 1,
    Obsolete = 2,
    Deleted = 4
}

/// <summary>
/// G2: Segment Directory Entry
/// 파일의 헤더 바로 뒤에 연속적으로 위치하는 디렉터리 영역의 단일 레코드입니다.
/// Logical ID 매핑은 SQLite가 담당하므로, 여기서는 물리적 상태와 크래시 복구를 위한 메타데이터만 유지합니다.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct RecordDirectoryEntry
{
    public readonly int RecordIndex;
    public readonly long Generation;
    
    // Torn-write 방어를 위한 각 벡터별 개별 Checksum (22항, 25항)
    public readonly uint PayloadCRC; 
    public readonly RecordFlags Flags; 
    
    // 구조체를 깔끔하게 맞추기 위한 Padding (총 32바이트 등으로 정렬 시 필요하면 확장)
    private readonly byte _padding1;
    private readonly ushort _padding2;

    public RecordDirectoryEntry(int recordIndex, long generation, uint payloadCRC, RecordFlags flags)
    {
        RecordIndex = recordIndex;
        Generation = generation;
        PayloadCRC = payloadCRC;
        Flags = flags;
        _padding1 = 0;
        _padding2 = 0;
    }
}
