<!--
sha: 202fc188
date: 2026-09-11T04:02:00Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: MySQL 8.4
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

Focused full-quality run after caching successful stored-view parses in the
same bounded concurrent cache used by ordinary statements. The immediately
preceding rerank measured fsdb at 475.11 μs; reusing the immutable definition
AST reduces it to 261.19 μs, a 45.0% improvement. The fsdb/MySQL ratio falls
from 7.53x to 4.10x.

```
BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD
  Job-IKCCTX : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3
```

| Method | Target | Mean | Error | StdDev | Allocated |
| --- | --- | ---: | ---: | ---: | ---: |
| ViewFilterLimit | fsdb | 261.19 μs | 1.969 μs | 0.511 μs | 537 B |
| ViewFilterLimit | mysql | 63.78 μs | 1.165 μs | 0.416 μs | 536 B |
