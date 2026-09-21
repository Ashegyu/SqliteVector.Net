namespace SqliteVector.Net.Tests;

using System;
using System.IO;
using System.Runtime.InteropServices;
using FluentAssertions;
using SqliteVector.Net.Catalog;
using SqliteVector.Net.Storage;
using Xunit;

public class ActiveSegmentWriterTests
{
    [Fact]
    public void G3_AppendOnly_ShouldGuaranteeAlignmentAndCRC()
    {
        // Arrange
        string testFile = Path.Combine(Path.GetTempPath(), $"segment_{Guid.NewGuid()}.vec");
        
        try
        {
            // 100차원(Payload 400B), Alignment 64 지정 (패딩 테스트 목적)
            var header = new SegmentHeader(
                segmentId: 1, 
                generation: 1, 
                dimensions: 100, 
                elementType: VectorElementType.Float32, 
                capacity: 10, 
                alignment: 64);
                
            // 400B를 64B로 정렬하면 448B여야 함
            header.VectorStride.Should().Be(448);

            using (var writer = new ActiveSegmentWriter(testFile, header))
            {
                var vec1 = new float[100];
                vec1[0] = 1.0f;
                vec1[99] = 99.0f;
                
                var vec2 = new float[100];
                vec2[0] = -1.0f;

                // Act
                int idx1 = writer.AppendVector(vec1, generation: 2);
                int idx2 = writer.AppendVector(vec2, generation: 2);

                // Assert
                idx1.Should().Be(0);
                idx2.Should().Be(1);
                writer.RecordCount.Should().Be(2);
            }

            // 디스크 파일 검증 (Torn Write 방어 및 패딩 확인)
            using var fs = new FileStream(testFile, FileMode.Open, FileAccess.Read);
            
            // 1. 파일 크기 검증 (Header + Directory + Vector0(448B) + Vector1(448B))
            // VectorRegion은 미리 세팅되어 있었음. 
            // 단, 마지막에 Vector1까지 썼으므로 파일 크기는 최소 VectorRegionOffset + 896B 이상.
            fs.Length.Should().BeGreaterThanOrEqualTo(header.VectorRegionOffset + (2 * 448));

            // 2. Vector0 주소 검증
            fs.Seek(header.VectorRegionOffset, SeekOrigin.Begin);
            var buffer = new byte[400];
            fs.ReadExactly(buffer, 0, 400);
            
            var floatSpan = MemoryMarshal.Cast<byte, float>(buffer);
            floatSpan[0].Should().Be(1.0f);
            floatSpan[99].Should().Be(99.0f);
            
            // 3. 디렉터리 0번 레코드 검증 (Generation 2, Committed 상태여야 함)
            fs.Seek(header.DirectoryOffset, SeekOrigin.Begin);
            var dirBuffer = new byte[Marshal.SizeOf<RecordDirectoryEntry>()];
            fs.ReadExactly(dirBuffer, 0, dirBuffer.Length);
            
            var entry = MemoryMarshal.Read<RecordDirectoryEntry>(dirBuffer);
            entry.RecordIndex.Should().Be(0);
            entry.Generation.Should().Be(2);
            entry.Flags.Should().Be(RecordFlags.Committed);
            // CRC가 0이 아님을 확인
            entry.PayloadCRC.Should().NotBe(0); 
        }
        finally
        {
            if (File.Exists(testFile)) File.Delete(testFile);
        }
    }
}
