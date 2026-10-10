# NuGet package consumer

This standalone F# console project references the `Fsdb` NuGet package. It
registers a scalar extension, opens an in-process connection, and executes
SQL without a server. It has no project reference to fsdb's source tree.

Before the first NuGet publication, run `just package-check` from the repository
root. That command builds the local package and restores this example from an
isolated package cache. Once `0.1.0-preview.1` is published, `dotnet run --project
examples/PackageConsumer` also works with the normal NuGet sources.
