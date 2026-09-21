```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
Intel Core Ultra 7 270K Plus 3.70GHz, 1 CPU, 24 logical and 24 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method              | Mean     | Error     | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|-------------------- |---------:|----------:|----------:|------:|--------:|----------:|------------:|
| Search_SingleThread | 3.642 ms | 1.5129 ms | 0.0829 ms |  1.00 |    0.03 |  26.77 KB |        1.00 |
| Search_MultiThread  | 3.183 ms | 0.2758 ms | 0.0151 ms |  0.87 |    0.02 |  37.28 KB |        1.39 |
