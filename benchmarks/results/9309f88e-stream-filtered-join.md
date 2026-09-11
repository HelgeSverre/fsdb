<!--
sha: 9309f88e
date: 2026-09-11T03:52:00Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: MySQL 8.4
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

Focused full-quality run after keeping a pushed base-table predicate lazy when
a join may stop at `LIMIT`. Physical index narrowing is unchanged, but fsdb no
longer evaluates and materializes every matching base row before the indexed
join can yield its first result. The immediately preceding run measured fsdb at
1,826.55 μs; the streaming path reduces it to 346.50 μs, a 5.27x improvement.
The fsdb/MySQL ratio falls from 6.57x to 1.25x.

```
BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD
  Job-NBAGCT : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3
```

| Method | Target | Mean | Error | StdDev | Allocated |
| --- | --- | ---: | ---: | ---: | ---: |
| JoinUsersOrders | fsdb | 346.5 μs | 2.32 μs | 0.83 μs | 809 B |
| JoinUsersOrders | mysql | 277.8 μs | 3.46 μs | 1.23 μs | 889 B |
