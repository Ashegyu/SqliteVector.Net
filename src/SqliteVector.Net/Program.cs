namespace SqliteVector.Net;

using System;
using System.Diagnostics;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("🚀 SqliteVector.NET 🚀");
        Console.WriteLine("Zero-dependency, Pure C# SIMD Vector Search built on SQLite\n");
        
        // 예시: OpenAI 임베딩 차원(1536) 사용
        int dimensions = 1536;
        using var store = new SqliteVectorStore("file::memory:?cache=shared", dimensions);
        var random = new Random(42); 
        
        int vectorCount = 50_000;
        Console.WriteLine($"[1] {vectorCount:N0}개의 랜덤 벡터(차원: {dimensions}) 저장 중...");
        
        string targetId = "doc-target-001";
        string targetMetadata = "이것이 우리가 찾던 원본 텍스트 데이터입니다!";
        float[] targetVector = new float[dimensions];

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < vectorCount; i++)
        {
            string id = (i == 25000) ? targetId : $"doc-{i}";
            string? meta = (i == 25000) ? targetMetadata : null;
            float[] vec = new float[dimensions];
            
            for (int j = 0; j < dimensions; j++) vec[j] = (float)(random.NextDouble() * 2 - 1);
            if (i == 25000) targetVector = vec;
            
            await store.UpsertAsync(id, vec, meta);
        }
        sw.Stop();
        Console.WriteLine($"✅ 저장 완료! (소요 시간: {sw.ElapsedMilliseconds} ms)\n");

        Console.WriteLine($"[2] DB에서 유사도 Top 3 검색 시작...");
        sw.Restart();
        var results = await store.SearchAsync(targetVector, topK: 3);
        sw.Stop();
        
        Console.WriteLine($"✅ 검색 완료! (소요 시간: {sw.ElapsedMilliseconds} ms)\n");
        Console.WriteLine("--- 검색 결과 ---");
        
        foreach (var result in results)
        {
            Console.WriteLine($"ID: {result.Id}");
            Console.WriteLine($"Score: {result.Score:F4}");
            Console.WriteLine($"Metadata: {result.Metadata ?? "null"}");
            Console.WriteLine("-");
        }
    }
}
