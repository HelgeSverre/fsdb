#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT

version="$(dotnet msbuild "$repo_root/src/Fsdb/Fsdb.fsproj" -getProperty:Version)"
cli_version="$(dotnet msbuild "$repo_root/src/Fsdb.Cli/Fsdb.Cli.fsproj" -getProperty:Version)"
if [ "$version" != "$cli_version" ]; then
    echo "Library and CLI versions differ: $version vs $cli_version" >&2
    exit 1
fi
dotnet pack "$repo_root/src/Fsdb/Fsdb.fsproj" -c Release --nologo -o "$scratch/packages"
package="$scratch/packages/Fsdb.$version.nupkg"
package_entries="$(unzip -Z1 "$package")"
nuspec="$(unzip -p "$package" Fsdb.nuspec)"
symbol_entries="$(unzip -Z1 "$scratch/packages/Fsdb.$version.snupkg")"

if grep -Eq '(^examples/|Fsdb\.Cli|Program\.fs)' <<< "$package_entries"; then
    echo "Library package contains an example or CLI entry point" >&2
    exit 1
fi
if grep -q 'dependency id="Argu"' <<< "$nuspec"; then
    echo "Library package depends on the CLI argument parser" >&2
    exit 1
fi
if ! grep -Fxq 'lib/net10.0/Fsdb.pdb' <<< "$symbol_entries"; then
    echo "Symbol package is missing Fsdb.pdb" >&2
    exit 1
fi

project="$repo_root/examples/PackageConsumer/PackageConsumer.fsproj"
if ! grep -q "Version=\"$version\"" "$project"; then
    echo "PackageConsumer must reference Fsdb $version" >&2
    exit 1
fi

export NUGET_PACKAGES="$scratch/cache"
cat > "$scratch/NuGet.Config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$scratch/packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local"><package pattern="Fsdb" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
EOF
dotnet restore "$project" --configfile "$scratch/NuGet.Config" --nologo
dotnet run --project "$project" --no-restore
