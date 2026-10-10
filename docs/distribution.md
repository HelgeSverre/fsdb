# Library distribution

`src/Fsdb/Fsdb.fsproj` produces the `Fsdb` NuGet library for .NET 10.
`src/Fsdb.Cli/Fsdb.Cli.fsproj` produces the server executable and is not part
of the library package. The first candidate is `0.1.0-preview.1`; the repository
has no earlier release tags. Its public embedding entry point is `Fsdb.Db`.
This is a prerelease interface: package consumers should expect API changes
before a stable `1.0` release.

Run `just check` and `just package-check` before releasing. The package check
builds both `.nupkg` and `.snupkg`, then restores
[`examples/PackageConsumer`](../examples/PackageConsumer/README.md) from an
isolated package cache and exercises an in-process SQL connection. To retain artifacts
for inspection, run:

```sh
dotnet pack src/Fsdb/Fsdb.fsproj -c Release -o artifacts/packages
```

Inspect the package's README, framework, dependencies, and repository commit
before publication. The package should contain the library DLL, not the CLI
entry point or its Argu dependency. The symbol package should contain
`Fsdb.pdb`. The version in `Fsdb.fsproj` and `Fsdb.Cli.fsproj` should match.

After reviewing and committing the exact release source, rebuild the package
from that commit. Publishing to nuget.org and pushing the first `v0.1.0-preview.1`
tag are separate release actions. The [NuGet CLI publish guide](https://learn.microsoft.com/en-us/nuget/quickstart/create-and-publish-a-package-using-the-dotnet-cli)
describes account and API-key setup. Do not reuse a package version after it
has been published; advance the preview number for later candidates.
