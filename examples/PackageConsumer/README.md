# NuGet package consumer

This standalone F# console project references the `Fsdb` NuGet package. It
registers a scalar extension, owns an in-process connection, executes a
parameterized query, reuses a prepared command for typed asynchronous results,
and consumes a commit event without a server. It has no project reference to
fsdb's source tree.

Run the example from the repository root.

```sh
dotnet run --project examples/PackageConsumer
```

This uses the version pinned in [PackageConsumer.fsproj](PackageConsumer.fsproj)
from your configured NuGet sources. Run `just package-check` to test current
source instead. It packs the library and restores this example from an
isolated local feed and package cache.
