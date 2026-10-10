# NuGet package consumer

This standalone F# console project references the `Fsdb` NuGet package. It
registers a scalar extension, owns an in-process connection, executes a
parameterized query, reuses a prepared command for typed asynchronous results,
and consumes a commit event without a server. It has no project reference to
fsdb's source tree.

Run `just package-check` from the repository root to build the current package
and restore this example from an isolated local package cache. After the
matching preview version is published, `dotnet run --project
examples/PackageConsumer` also works with normal NuGet sources.
