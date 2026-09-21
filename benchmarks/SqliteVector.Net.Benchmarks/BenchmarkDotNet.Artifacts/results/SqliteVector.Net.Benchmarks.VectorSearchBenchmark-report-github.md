```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9457/25H2/2025Update/HudsonValley2)
Intel Core Ultra 7 270K Plus 3.70GHz, 1 CPU, 24 logical and 24 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method              | Mean     | Error     | StdDev    | Ratio | Gen0    | Allocated | Alloc Ratio |
|-------------------- |---------:|----------:|----------:|------:|--------:|----------:|------------:|
| Search_SingleThread | 1.868 ms | 0.3839 ms | 0.0210 ms |  1.00 | 15.6250 | 317.85 KB |        1.00 |
| Search_MultiThread  | 1.720 ms | 0.2067 ms | 0.0113 ms |  0.92 | 15.6250 | 327.05 KB |        1.03 |
