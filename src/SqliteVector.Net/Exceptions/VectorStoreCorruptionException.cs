namespace SqliteVector.Net.Exceptions;

using System;

public enum CorruptionKind
{
    InvalidMagic,
    HeaderChecksum,
    InvalidLayout,
    PayloadChecksum,
    TruncatedSegment,
    ContractMismatch,
    UnknownRecordFlags
}

public class VectorStoreCorruptionException : Exception
{
    public CorruptionKind Kind { get; }
    public long SegmentId { get; }
    public string FilePath { get; }
    public int? RecordIndex { get; }

    public VectorStoreCorruptionException(
        CorruptionKind kind, 
        long segmentId, 
        string filePath, 
        string message, 
        int? recordIndex = null) 
        : base($"Segment {segmentId} [{kind}]: {message} (File: {filePath}, Record: {recordIndex?.ToString() ?? "N/A"})")
    {
        Kind = kind;
        SegmentId = segmentId;
        FilePath = filePath;
        RecordIndex = recordIndex;
    }
}
