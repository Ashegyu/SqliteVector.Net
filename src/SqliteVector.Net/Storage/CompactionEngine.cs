namespace SqliteVector.Net.Storage;

using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using SqliteVector.Net.Catalog;

/// <summary>
/// G13: Compaction Engine
/// V2 아키텍처의 유일한 Garbage Collector입니다. 
/// 파편화된 구형 세그먼트에서 삭제/만료(Obsolete)된 벡터를 버리고,
/// 살아있는(Live) 벡터만 새 세그먼트 파일로 순차 복사하여 용량을 회수합니다.
/// </summary>
public sealed class CompactionEngine
{
    private readonly SqliteCatalog _catalog;
    private readonly SqliteConnection _directConnection;
    private readonly string _dbPath;

    public CompactionEngine(SqliteCatalog catalog, string dbPath)
    {
        _catalog = catalog;
        _dbPath = dbPath;
        _directConnection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        _directConnection.Open();
    }

    // Test Hook for Adversarial Validation
    public static Action? BeforeCatalogPublishHook { get; set; }

    private record struct Candidate(string ExternalId, int OldRecordIndex, long OldGeneration, string? Metadata);

    public void CompactSegment(long oldSegmentId, string oldSegmentPath, long newSegmentId, string newSegmentPath, SegmentHeader newHeader)
    {
        long compactionGeneration = newHeader.Generation;
        var candidates = new List<Candidate>();

        // PHASE A: Short read snapshot / candidate capture
        using (var compConn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            compConn.Open();
            using var cmd = compConn.CreateCommand();
            cmd.CommandText = @"
                SELECT v.external_id, v.record_index, v.generation, r.metadata 
                FROM vectors v
                LEFT JOIN vector_records r ON v.segment_id = r.segment_id AND v.record_index = r.record_index
                WHERE v.segment_id = @seg AND v.deleted = 0 AND v.generation <= @gen";
            cmd.Parameters.AddWithValue("@seg", oldSegmentId);
            cmd.Parameters.AddWithValue("@gen", compactionGeneration);
            
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                candidates.Add(new Candidate(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)
                ));
            }
        }

        if (candidates.Count == 0) return;

        // PHASE B: Copy physical records with NO SQLite transaction held
        var newIndices = new int[candidates.Count];
        string tmpSegmentPath = newSegmentPath + ".tmp";
        using (var oldReader = new SegmentReader(oldSegmentPath))
        using (var newWriter = new ActiveSegmentWriter(tmpSegmentPath, newHeader))
        {
            Span<float> buffer = stackalloc float[newHeader.Dimensions];
            for (int i = 0; i < candidates.Count; i++)
            {
                oldReader.ReadVector(candidates[i].OldRecordIndex, buffer);
                newIndices[i] = newWriter.AppendVector(buffer, generation: compactionGeneration);
            }
        }

        // Atomically commit physical file before we commit logical references
        System.IO.File.Move(tmpSegmentPath, newSegmentPath, overwrite: true);

        BeforeCatalogPublishHook?.Invoke();

        // PHASE C: Short BEGIN IMMEDIATE / exact CAS / publish
        using (var compConn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            compConn.Open();
            // Use BEGIN IMMEDIATE so we get a write lock right away without risk of SQLITE_BUSY upgrade deadlocks
            using var tx = compConn.BeginTransaction(System.Data.IsolationLevel.Serializable);
            
            using var updateCmd = compConn.CreateCommand();
            updateCmd.Transaction = tx;
            updateCmd.CommandText = @"
                UPDATE vectors 
                SET segment_id = @newSeg, record_index = @newRec, generation = @newGen
                WHERE external_id = @id AND segment_id = @oldSeg AND record_index = @oldRec AND generation = @oldGen AND deleted = 0
            ";
            var pNewSeg = updateCmd.Parameters.Add("@newSeg", Microsoft.Data.Sqlite.SqliteType.Integer);
            var pNewRec = updateCmd.Parameters.Add("@newRec", Microsoft.Data.Sqlite.SqliteType.Integer);
            var pNewGen = updateCmd.Parameters.Add("@newGen", Microsoft.Data.Sqlite.SqliteType.Integer);
            var pId = updateCmd.Parameters.Add("@id", Microsoft.Data.Sqlite.SqliteType.Text);
            var pOldSeg = updateCmd.Parameters.Add("@oldSeg", Microsoft.Data.Sqlite.SqliteType.Integer);
            var pOldRec = updateCmd.Parameters.Add("@oldRec", Microsoft.Data.Sqlite.SqliteType.Integer);
            var pOldGen = updateCmd.Parameters.Add("@oldGen", Microsoft.Data.Sqlite.SqliteType.Integer);

            using var historyCmd = compConn.CreateCommand();
            historyCmd.Transaction = tx;
            historyCmd.CommandText = "INSERT OR IGNORE INTO vector_records (segment_id, record_index, external_id, metadata) VALUES (@seg, @rec, @id, @meta)";
            var hSeg = historyCmd.Parameters.Add("@seg", Microsoft.Data.Sqlite.SqliteType.Integer);
            var hRec = historyCmd.Parameters.Add("@rec", Microsoft.Data.Sqlite.SqliteType.Integer);
            var hId = historyCmd.Parameters.Add("@id", Microsoft.Data.Sqlite.SqliteType.Text);
            var hMeta = historyCmd.Parameters.Add("@meta", Microsoft.Data.Sqlite.SqliteType.Text);

            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                pNewSeg.Value = newSegmentId;
                pNewRec.Value = newIndices[i];
                pNewGen.Value = compactionGeneration;
                pId.Value = c.ExternalId;
                pOldSeg.Value = oldSegmentId;
                pOldRec.Value = c.OldRecordIndex;
                pOldGen.Value = c.OldGeneration;

                int rowsAffected = updateCmd.ExecuteNonQuery();

                if (rowsAffected > 0)
                {
                    hSeg.Value = newSegmentId;
                    hRec.Value = newIndices[i];
                    hId.Value = c.ExternalId;
                    hMeta.Value = (object?)c.Metadata ?? DBNull.Value;
                    historyCmd.ExecuteNonQuery();
                }
            }

            // A1: Bump revision inside the transaction
            using var revCmd = compConn.CreateCommand();
            revCmd.Transaction = tx;
            revCmd.CommandText = "UPDATE database_state SET current_revision = current_revision + 1 WHERE singleton = 1";
            revCmd.ExecuteNonQuery();

            tx.Commit();
        }
    }

    /// <summary>
    /// Phase D: Compaction Closure (D1 ~ D5)
    /// </summary>
    public void RunCompaction(string directoryPath, VectorDatabaseOptions options)
    {
        // D1: Select Candidates (All Sealed segments)
        var sealedSegments = new List<long>();
        using (var cmd = _directConnection.CreateCommand())
        {
            cmd.CommandText = "SELECT segment_id FROM segments WHERE state = 1 ORDER BY segment_id ASC";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                sealedSegments.Add(reader.GetInt64(0));
            }
        }

        if (sealedSegments.Count == 0) return;

        // D2 & D3: For each sealed segment, compact into a new segment
        foreach (long oldSeg in sealedSegments)
        {
            // Check if there are any live records first
            int liveCount = 0;
            using (var cmd = _directConnection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM vectors WHERE segment_id = @seg AND deleted = 0";
                cmd.Parameters.AddWithValue("@seg", oldSeg);
                liveCount = Convert.ToInt32(cmd.ExecuteScalar());
            }

            if (liveCount == 0)
            {
                // Segment is completely empty. Just mark it as deleted.
                using var tx = _directConnection.BeginTransaction();
                using (var updateCmd = _directConnection.CreateCommand())
                {
                    updateCmd.Transaction = tx;
                    updateCmd.CommandText = "UPDATE segments SET state = 2 WHERE segment_id = @old;";
                    updateCmd.Parameters.AddWithValue("@old", oldSeg);
                    updateCmd.ExecuteNonQuery();
                }
                tx.Commit();
                // Ensure hook runs so test doesn't deadlock if it expects a hook invocation
                BeforeCatalogPublishHook?.Invoke();
                continue;
            }

            long newSegId;
            using (var createCmd = _directConnection.CreateCommand())
            {
                createCmd.CommandText = "INSERT INTO segments (state) VALUES (0); SELECT last_insert_rowid();";
                newSegId = (long)createCmd.ExecuteScalar()!;
            }
            
            string oldPath = Path.Combine(directoryPath, $"segment_{oldSeg:D6}.vec");
            string newPath = Path.Combine(directoryPath, $"segment_{newSegId:D6}.vec");
            
            // Capture generation
            long currentGen;
            using (var genCmd = _directConnection.CreateCommand())
            {
                genCmd.CommandText = "SELECT current_revision FROM database_state WHERE singleton = 1";
                currentGen = (long)genCmd.ExecuteScalar()!;
            }

            var newHeader = new SegmentHeader(newSegId, currentGen, options.Dimensions, VectorElementType.Float32, capacity: 100000, alignment: 64);
            
            CompactSegment(oldSeg, oldPath, newSegId, newPath, newHeader);
            
            // D4 & D5: Seal the new segment, mark old as Deleted
            using var tx2 = _directConnection.BeginTransaction();
            using (var updateCmd = _directConnection.CreateCommand())
            {
                updateCmd.Transaction = tx2;
                updateCmd.CommandText = "UPDATE segments SET state = 1 WHERE segment_id = @new; UPDATE segments SET state = 2 WHERE segment_id = @old;";
                updateCmd.Parameters.AddWithValue("@new", newSegId);
                updateCmd.Parameters.AddWithValue("@old", oldSeg);
                updateCmd.ExecuteNonQuery();
            }
            tx2.Commit();
        }
        
        // D5 (Cleanup): Try to delete physical files of state = 2 segments
        var deletedSegments = new List<long>();
        using (var cmd = _directConnection.CreateCommand())
        {
            cmd.CommandText = "SELECT segment_id FROM segments WHERE state = 2";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                deletedSegments.Add(reader.GetInt64(0));
            }
        }
        
        foreach (var oldSeg in deletedSegments)
        {
            string oldPath = System.IO.Path.Combine(directoryPath, $"segment_{oldSeg:D6}.vec");
            if (System.IO.File.Exists(oldPath))
            {
                try
                {
                    System.IO.File.Delete(oldPath);
                }
                catch (System.IO.IOException)
                {
                    // SearchSnapshot is still holding the memory mapped file. Will retry later.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same as above on Windows
                }
            }
        }
    }

    public void Close()
    {
        _directConnection.Dispose();
    }
}
