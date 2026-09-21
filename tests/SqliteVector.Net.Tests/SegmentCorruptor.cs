using System;
using System.IO;
using System.Runtime.InteropServices;
using SqliteVector.Net.Storage;

namespace SqliteVector.Net.Tests;

public static class SegmentCorruptor
{
    public static unsafe void MutateHeader(
        string path, 
        Func<SegmentHeader, SegmentHeader> mutator, 
        bool fixChecksum = true, 
        bool corruptChecksum = false)
    {
        byte[] data = File.ReadAllBytes(path);
        
        fixed (byte* ptr = data)
        {
            SegmentHeader* h = (SegmentHeader*)ptr;
            *h = mutator(*h);
            
            if (fixChecksum)
            {
                ReadOnlySpan<byte> bytes = new ReadOnlySpan<byte>(ptr, 124);
                uint crc = System.IO.Hashing.Crc32.HashToUInt32(bytes);
                uint* chk = (uint*)(ptr + 124);
                *chk = crc;
            }
            if (corruptChecksum)
            {
                uint* chkPtr = (uint*)(ptr + 124); // assuming checksum is last 4 bytes
                *chkPtr ^= 0xFFFFFFFF;
            }
        }
        
        File.WriteAllBytes(path, data);
    }

    public static void FlipPayloadByte(string path, int recordIndex, int payloadOffset)
    {
        byte[] data = File.ReadAllBytes(path);
        
        unsafe
        {
            fixed (byte* ptr = data)
            {
                SegmentHeader* h = (SegmentHeader*)ptr;
                long vecRegion = h->VectorRegionOffset;
                int stride = h->VectorStride;
                long targetOffset = vecRegion + (recordIndex * (long)stride) + payloadOffset;
                
                if (targetOffset < data.Length)
                {
                    data[targetOffset] ^= 0x01;
                }
            }
        }
        File.WriteAllBytes(path, data);
    }
    
    public static void MutateDirectoryEntry(string path, int recordIndex, Func<RecordDirectoryEntry, RecordDirectoryEntry> mutator)
    {
        byte[] data = File.ReadAllBytes(path);
        
        unsafe
        {
            fixed (byte* ptr = data)
            {
                SegmentHeader* h = (SegmentHeader*)ptr;
                long dirOffset = h->DirectoryOffset;
                long targetOffset = dirOffset + (recordIndex * 32L);
                
                RecordDirectoryEntry* entry = (RecordDirectoryEntry*)(ptr + targetOffset);
                *entry = mutator(*entry);
            }
        }
        File.WriteAllBytes(path, data);
    }

    public static void Truncate(string path, long length)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write);
        fs.SetLength(length);
    }
}
