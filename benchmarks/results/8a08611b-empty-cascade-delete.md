<!--
sha: 8a08611b
date: 2026-09-11T03:43:00Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: MySQL 8.4
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

Focused full-quality run after making an empty cascade-delete set an immediate
no-op. The immediately preceding run measured conflict-free `REPLACE` at
1,402.11 μs; the guard reduces it to 224.81 μs, a 6.24x improvement. The
fsdb/MySQL ratio falls from 11.35x to 1.82x.

```
BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD
  Job-LIUFCE : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3
```

| Method | Target | Mean | Error | StdDev | Gen0 | Allocated |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| ReplaceNewRow | fsdb | 224.8 μs | 28.32 μs | 7.36 μs | - | 1.25 KB |
| ReplaceNewRow | mysql | 123.6 μs | 1.56 μs | 0.55 μs | 0.1221 | 1.25 KB |
