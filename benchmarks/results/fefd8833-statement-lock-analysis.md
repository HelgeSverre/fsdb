<!--
sha: fefd8833
date: 2026-09-11T03:32:00Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: MySQL 8.4
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

Focused full-quality run after avoiding redundant privilege traversal for
statements whose direct lock-access collector is exhaustive. The broad-report
baseline was 387.21 μs for fsdb and 53.69 μs for MySQL; this run measures
293.55 μs and 41.47 μs respectively. The fsdb latency improvement is 24.2%.

```
BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD
  Job-CGMOET : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3
```

| Method | Target | Mean | Error | StdDev | Gen0 | Allocated |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| PointSelectByPk | fsdb | 293.55 μs | 46.648 μs | 16.635 μs | - | 881 B |
| PointSelectByPk | mysql | 41.47 μs | 0.182 μs | 0.047 μs | 0.0610 | 880 B |
