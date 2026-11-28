# Library distribution

`src/Fsdb/Fsdb.fsproj` produces the `Fsdb` NuGet library for .NET 10.
`src/Fsdb.Cli/Fsdb.Cli.fsproj` produces the server executable and is not part
of the library package. Install the latest published stable version with
`dotnet add package Fsdb`, or pin a version from the
[NuGet version list](https://www.nuget.org/packages/Fsdb/).
The public embedding entry point is `Fsdb.Db`. The project version on
`main` can remain unchanged while development continues. Use the tagged
source and [release notes](releases) to check what a published package contains.
Later releases may evolve the API and storage format. Test a copy of durable
data before upgrading.

Run `just check` and `just package-check` before releasing. The package check
builds both `.nupkg` and `.snupkg`, then restores
[`examples/PackageConsumer`](../examples/PackageConsumer/README.md) from an
isolated package cache and exercises scalar extensions, prepared typed
queries, and commit subscriptions through an in-process connection.
To retain artifacts for inspection, run:

```sh
dotnet pack src/Fsdb/Fsdb.fsproj -c Release -o artifacts/packages
```

Inspect the package's README, framework, dependencies, and repository commit
before publication. The package should contain the library DLL, not the CLI
entry point or its Argu dependency. The symbol package should contain
`Fsdb.pdb`. The version in `Fsdb.fsproj` and `Fsdb.Cli.fsproj` should match.

Releases use [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).
The NuGet account owner must register a GitHub Actions trusted-publishing policy:

- Owner: `helgesverre`
- Repository owner: `HelgeSverre`
- Repository: `fsdb`
- Workflow file: `publish-nuget.yml`
- Environment: leave empty
- Scope: push new packages and package versions, restricted to `Fsdb`

After reviewing and committing the exact release source, push the tag matching
the project version. The tag runs
`.github/workflows/publish-nuget.yml`, which checks the version, builds and tests,
verifies package consumption, and packs from that commit. It then exchanges a
GitHub OIDC token for a short-lived NuGet key and pushes the package and symbol
package. No long-lived NuGet key is stored in the repository or GitHub Actions.

NuGet package versions cannot be reused after publication. Advance the version
in the library, CLI, and package consumer before tagging a later release.
