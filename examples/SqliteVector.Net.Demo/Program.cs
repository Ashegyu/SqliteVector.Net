using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using SqliteVector.Net;
using SqliteVector.Net.Catalog;

namespace SqliteVector.Net.Demo;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("🚀 SqliteVector.NET v2.0 Interactive Demo");
        Console.WriteLine("-------------------------------------------");

        string dbDir = Path.Combine(Environment.CurrentDirectory, "demo_knowledge");
        if (Directory.Exists(dbDir))
        {
            Directory.Delete(dbDir, true);
        }
        Directory.CreateDirectory(dbDir);

        int dimensions = 384; // Standard size for many small embedding models
        
        var options = new VectorDatabaseOptions 
        { 
            Dimensions = dimensions,
            Metric = VectorMetric.Cosine,
            SegmentCapacity = 100_000
        };

        Console.WriteLine("📦 Initializing VectorDatabase...");
        await using var db = await VectorDatabase.OpenAsync(dbDir, options);

        Console.WriteLine("📝 Generating and inserting 10,000 dummy vectors...");
        var sw = Stopwatch.StartNew();
        
        var rng = new Random(42);
        for (int i = 0; i < 10000; i++)
        {
            float[] vec = new float[dimensions];
            for (int d = 0; d < dimensions; d++)
            {
                vec[d] = (float)(rng.NextDouble() * 2 - 1);
            }
            
            // Insert into the database
            await db.UpsertAsync($"doc-{i}", vec, metadata: $"{{\"title\": \"Document #{i}\"}}");
            
            if (i % 2500 == 0 && i > 0)
                Console.WriteLine($"   -> Inserted {i} records...");
        }
        
        sw.Stop();
        Console.WriteLine($"✅ Inserted 10,000 vectors in {sw.ElapsedMilliseconds} ms!\n");

        // Let's do a search!
        Console.WriteLine("🔍 Searching for Top-5 nearest neighbors to a random query...");
        float[] queryVec = new float[dimensions];
        for (int d = 0; d < dimensions; d++) queryVec[d] = (float)(rng.NextDouble() * 2 - 1);

        sw.Restart();
        var results = await db.SearchAsync(queryVec, new VectorSearchOptions { TopK = 5 });
        sw.Stop();

        Console.WriteLine($"⏱️  Search completed in {sw.ElapsedMilliseconds} ms. Results:");
        
        foreach (var res in results)
        {
            Console.WriteLine($"   🏆 ID: {res.Id,-12} | Score: {res.Score:F4} | Meta: {res.Metadata}");
        }

        Console.WriteLine("\n🧹 Running background compaction...");
        await db.CompactAsync();
        Console.WriteLine("✅ Demo completed successfully!");
    }
}
