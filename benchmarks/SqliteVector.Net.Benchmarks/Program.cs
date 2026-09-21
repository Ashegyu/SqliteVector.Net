using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Jobs;
using System.IO;
using System.Threading.Tasks;
using SqliteVector.Net;
using SqliteVector.Net.Catalog;
using Microsoft.Data.Sqlite;

namespace SqliteVector.Net.Benchmarks
{
    [ShortRunJob]
    [MemoryDiagnoser]
    public class VectorSearchBenchmark
    {
        private VectorDatabase _db;
        private float[] _query;
        private string _testDir;

        [GlobalSetup]
        public async Task Setup()
        {
            _testDir = Path.Combine(Path.GetTempPath(), $"bench_{Guid.NewGuid()}");
            _db = await VectorDatabase.OpenAsync(_testDir, new VectorDatabaseOptions { Dimensions = 384, Metric = VectorMetric.DotProduct });
            
            var rand = new Random(42);
            for (int i = 0; i < 10000; i++)
            {
                var vec = new float[384];
                for (int d = 0; d < 384; d++) vec[d] = (float)rand.NextDouble();
                await _db.UpsertAsync($"doc-{i}", vec);
            }

            _query = new float[384];
            for (int d = 0; d < 384; d++) _query[d] = (float)rand.NextDouble();
        }

        [Benchmark(Baseline = true)]
        public async Task Search_SingleThread()
        {
            await _db.SearchAsync(_query, new VectorSearchOptions { TopK = 10, UseParallelSearch = false });
        }

        [Benchmark]
        public async Task Search_MultiThread()
        {
            await _db.SearchAsync(_query, new VectorSearchOptions { TopK = 10, UseParallelSearch = true });
        }

        [GlobalCleanup]
        public async Task Cleanup()
        {
            await _db.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_testDir)) Directory.Delete(_testDir, true);
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("G11: SqliteVector.Net V2 Benchmarks (Parallel vs Single)");
            BenchmarkRunner.Run<VectorSearchBenchmark>();
        }
    }
}
