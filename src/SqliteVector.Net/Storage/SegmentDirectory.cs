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
/// 단일 벡터의 상태를 나타내는 Directory Entry (고정 32 바이트)
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 32)]
public struct RecordDirectoryEntry
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
