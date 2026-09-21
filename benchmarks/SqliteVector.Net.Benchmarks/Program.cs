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
        [Params(384, 1536)]
        public int Dimensions { get; set; }

        private VectorDatabase _db;
        private float[] _query;
        private string _testDir;

        [GlobalSetup]
        public async Task Setup()
        {
            _testDir = Path.Combine(Path.GetTempPath(), $"bench_{Guid.NewGuid()}");
            _db = await VectorDatabase.OpenAsync(_testDir, new VectorDatabaseOptions { Dimensions = this.Dimensions, Metric = VectorMetric.DotProduct });
            
            var rand = new Random(42);
            // Insert 100,000 vectors for a realistic benchmark!
            for (int i = 0; i < 100_000; i++)
            {
                var vec = new float[this.Dimensions];
                for (int d = 0; d < this.Dimensions; d++) vec[d] = (float)rand.NextDouble();
                await _db.UpsertAsync($"doc-{i}", vec);
            }

            _query = new float[this.Dimensions];
            for (int d = 0; d < this.Dimensions; d++) _query[d] = (float)rand.NextDouble();
        }

        [Benchmark(Baseline = true)]
        public async Task<VectorSearchResult[]> Search_SingleThread()
        {
            return await _db.SearchAsync(_query, new VectorSearchOptions { TopK = 10, UseParallelSearch = false });
        }

        [Benchmark]
        public async Task<VectorSearchResult[]> Search_MultiThread()
        {
            return await _db.SearchAsync(_query, new VectorSearchOptions { TopK = 10, UseParallelSearch = true });
        }

        [Benchmark(OperationsPerInvoke = 100)]
        public async Task MaxLoad_100_Concurrent_SingleThread_Engine()
        {
            // 웹 서버처럼 100개의 동시 요청(Concurrent Requests)이 쏟아지는 상황을 시뮬레이션
            var tasks = new Task[100];
            for (int i = 0; i < 100; i++)
            {
                // 트래픽이 많을 때는 각 요청을 단일 스레드로 처리하는 것이 
                // ThreadPool 컨텐션(경합)을 줄여 전체적인 Throughput(처리량)이 높아집니다.
                tasks[i] = _db.SearchAsync(_query, new VectorSearchOptions { TopK = 10, UseParallelSearch = false });
            }
            await Task.WhenAll(tasks);
        }

        [Benchmark(OperationsPerInvoke = 100)]
        public async Task MaxLoad_100_Concurrent_MultiThread_Engine()
        {
            var tasks = new Task[100];
            for (int i = 0; i < 100; i++)
            {
                tasks[i] = _db.SearchAsync(_query, new VectorSearchOptions { TopK = 10, UseParallelSearch = true });
            }
            await Task.WhenAll(tasks);
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
