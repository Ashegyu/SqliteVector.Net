using System;
using System.Threading.Tasks;
using SqliteVector.Net;
using SqliteVector.Net.Storage;
using SqliteVector.Net.Catalog;

namespace ChildRunner;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length < 2) return;
        
        string failurePointStr = args[0];
        string testDir = args[1];
        
        var point = Enum.Parse<StorageFailurePoint>(failurePointStr);
        
        FailureInjector.Hook = p =>
        {
            if (p == point)
            {
                Console.WriteLine("CRASH_NOW");
                while(true) System.Threading.Thread.Sleep(100);
            }
        };

        var options = new VectorDatabaseOptions { Dimensions = 2, Metric = VectorMetric.DotProduct, NormalizeVectors = false };
        var db = await VectorDatabase.OpenAsync(testDir, options);
        await db.UpsertAsync("A", new float[] { 0, 1 }, "NEW");
    }
}
