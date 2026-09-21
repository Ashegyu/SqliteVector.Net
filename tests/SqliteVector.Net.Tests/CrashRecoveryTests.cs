using System;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using SqliteVector.Net;
using SqliteVector.Net.Storage;
using SqliteVector.Net.Catalog;
using System.Linq;

namespace SqliteVector.Net.Tests;

public class CrashRecoveryTests : IAsyncLifetime
{
    private string _testDir = "";

    public Task InitializeAsync()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "SqliteVectorNet_CrashTests_" + Guid.NewGuid().ToString());
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true); } catch { }
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(StorageFailurePoint.AfterPhysicalCommitBeforeLogicalCommit, "OLD")]
    [InlineData(StorageFailurePoint.InsideLogicalTransaction, "OLD")]
    [InlineData(StorageFailurePoint.AfterLogicalCommitBeforeSnapshotPublish, "NEW")]
    public async Task CrashRecovery_Should_Yield_Correct_State(StorageFailurePoint point, string expectedMeta)
    {
        // 1. Initial State Setup
        var options = new VectorDatabaseOptions { Dimensions = 2, Metric = VectorMetric.DotProduct, NormalizeVectors = false };
        await using (var db = await VectorDatabase.OpenAsync(_testDir, options))
        {
            await db.UpsertAsync("A", new float[] { 1, 0 }, "OLD");
        }

        string runnerProjPath = Path.GetFullPath(@"..\..\..\..\..\tests\ChildRunner\ChildRunner.csproj");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --no-build --project \"{runnerProjPath}\" -- {point} \"{_testDir}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        var process = Process.Start(psi)!;
        
        // Wait for CRASH_NOW signal
        bool crashed = false;
        while (!process.StandardOutput.EndOfStream)
        {
            var line = await process.StandardOutput.ReadLineAsync();
            if (line == "CRASH_NOW")
            {
                process.Kill(entireProcessTree: true);
                crashed = true;
                break;
            }
        }
        process.WaitForExit();

        if (!crashed && process.ExitCode != 0)
        {
            string err = await process.StandardError.ReadToEndAsync();
            string outStr = await process.StandardOutput.ReadToEndAsync();
            throw new Exception($"Child process failed to build/run! ExitCode={process.ExitCode}\nSTDOUT: {outStr}\nSTDERR: {err}");
        }

        // 3. Verify State after crash
        await using var db2 = await VectorDatabase.OpenAsync(_testDir, options);
        var results = await db2.SearchAsync(new float[] { 0, 1 }, new VectorSearchOptions { TopK = 1 }); // Both OLD and NEW can be found depending on expectation, but wait, OLD is [1, 0] and NEW is [0, 1].
        // Just search for [1,1] so it finds A.
        var resultsAll = await db2.SearchAsync(new float[] { 1, 1 }, new VectorSearchOptions { TopK = 5 });
        var resultsList = resultsAll.ToList();

        foreach(var r in resultsList)
        {
            Console.WriteLine($"Result: ID={r.Id}, Score={r.Score}, Meta={r.Metadata}");
        }

        resultsList.Should().HaveCount(1);
        resultsList[0].Id.Should().Be("A");
        resultsList[0].Metadata.Should().Be(expectedMeta);
    }
}
