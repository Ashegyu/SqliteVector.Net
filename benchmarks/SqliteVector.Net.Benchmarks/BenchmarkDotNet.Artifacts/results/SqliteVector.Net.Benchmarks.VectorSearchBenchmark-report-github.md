```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
Intel Core Ultra 7 270K Plus 3.70GHz, 1 CPU, 24 logical and 24 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                     | Dimensions | Mean       | Error     | StdDev   | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------------------- |----------- |-----------:|----------:|---------:|------:|--------:|-------:|----------:|------------:|
| **Search_SingleThread**                        | **384**        |   **517.8 μs** | **120.96 μs** |  **6.63 μs** |  **1.00** |    **0.02** |      **-** |  **13.32 KB** |        **1.00** |
| Search_MultiThread                         | 384        |   212.4 μs |  21.89 μs |  1.20 μs |  0.41 |    0.00 | 1.4648 |  27.02 KB |        2.03 |
| MaxLoad_100_Concurrent_SingleThread_Engine | 384        |   448.9 μs |  32.30 μs |  1.77 μs |  0.87 |    0.01 |      - |  13.33 KB |        1.00 |
| MaxLoad_100_Concurrent_MultiThread_Engine  | 384        |   215.5 μs |  11.15 μs |  0.61 μs |  0.42 |    0.00 | 1.2500 |  27.43 KB |        2.06 |
|                                            |            |            |           |          |       |         |        |           |             |
| **Search_SingleThread**                        | **1536**       | **2,925.2 μs** | **193.74 μs** | **10.62 μs** |  **1.00** |    **0.00** |      **-** |  **13.32 KB** |        **1.00** |
| Search_MultiThread                         | 1536       |   915.3 μs | 123.24 μs |  6.76 μs |  0.31 |    0.00 | 0.9766 |  31.05 KB |        2.33 |
| MaxLoad_100_Concurrent_SingleThread_Engine | 1536       | 2,844.8 μs | 872.60 μs | 47.83 μs |  0.97 |    0.01 |      - |  13.33 KB |        1.00 |
| MaxLoad_100_Concurrent_MultiThread_Engine  | 1536       |   915.7 μs | 489.92 μs | 26.85 μs |  0.31 |    0.01 |      - |  30.86 KB |        2.32 |
