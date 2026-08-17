# fsdb — MySQL-compatible database server in F#

MYSQL := "/opt/homebrew/opt/mysql-client/bin/mysql"
PORT := "3307"

# Show available recipes
default:
    @just --list --unsorted

# === Server ===

# Run the server, passing flags through (--port, --listen)
[group('server')]
run *ARGS:
    dotnet run --project src/Fsdb -- {{ ARGS }}

# Open a mysql shell against a running server
[group('server')]
client port=PORT:
    {{ MYSQL }} --protocol=tcp -h127.0.0.1 -P{{ port }} -uroot

# Quick liveness probe against a running server
[group('server')]
smoke port=PORT:
    {{ MYSQL }} --protocol=tcp -h127.0.0.1 -P{{ port }} -uroot -e 'SELECT 1; SELECT @@version;'

# === QA ===

# Run the Expecto test suite
[group('qa')]
test:
    dotnet run --project tests/Fsdb.Tests

# Build + tests
[group('qa')]
check: build test

# Branch coverage over the full suite. Expecto has no built-in coverage, so
# this instruments the built Fsdb.dll the test assembly loads, via the
# coverlet.console global tool (install once with
# `dotnet tool install -g coverlet.console`). Report lands in
# coverage/coverage.cobertura.xml (cobertura carries the branch data).
[group('qa')]
coverage:
    #!/usr/bin/env bash
    set -euo pipefail
    export PATH="$PATH:$HOME/.dotnet/tools"
    if ! command -v coverlet >/dev/null 2>&1; then
        echo "error: coverlet.console isn't installed — run: dotnet tool install -g coverlet.console" >&2
        exit 1
    fi
    dotnet_bin="$(command -v dotnet)"
    # Homebrew's dotnet lives outside the global-tool apphost's default
    # search path (it needs DOTNET_ROOT); derive it from the resolved
    # dotnet binary, leaving it alone where the default search already works.
    if [ -z "${DOTNET_ROOT:-}" ]; then
        resolved="$(readlink -f "$dotnet_bin" 2>/dev/null || echo "$dotnet_bin")"
        candidate="$(dirname "$(dirname "$resolved")")/libexec"
        if [ -d "$candidate/shared/Microsoft.NETCore.App" ]; then
            export DOTNET_ROOT="$candidate"
        fi
    fi
    dotnet build tests/Fsdb.Tests -c Debug -v q
    coverlet "tests/Fsdb.Tests/bin/Debug/net10.0/Fsdb.Tests.dll" \
        -t "$dotnet_bin" \
        -a "tests/Fsdb.Tests/bin/Debug/net10.0/Fsdb.Tests.dll" \
        --include "[Fsdb]*" \
        -f cobertura \
        -o coverage/coverage

# === Build ===

# Build the whole solution
[group('build')]
build:
    dotnet build

# Remove build artifacts
[group('build')]
clean:
    dotnet clean -v q
    rm -rf src/Fsdb/bin src/Fsdb/obj tests/Fsdb.Tests/bin tests/Fsdb.Tests/obj

# === Install ===

# Install fsdb globally as a single binary (framework-dependent)
[group('install')]
install dest="~/.local/bin":
    dotnet publish src/Fsdb -c Release -o src/Fsdb/bin/dist -p:PublishSingleFile=true --self-contained false -v q
    mkdir -p {{ dest }}
    install -m 0755 src/Fsdb/bin/dist/Fsdb {{ dest }}/fsdb
    @echo "Installed {{ dest }}/fsdb — try: fsdb --help"

# Remove the globally installed fsdb
[group('install')]
uninstall dest="~/.local/bin":
    rm -f {{ dest }}/fsdb
    @echo "Removed {{ dest }}/fsdb"

# === Bench ===
# fsdb vs a native MySQL 8.4, run ad hoc (no brew services / launchd).

MYSQLD := "/opt/homebrew/opt/mysql@8.4/bin/mysqld"
MYSQLADMIN := "/opt/homebrew/opt/mysql@8.4/bin/mysqladmin"
BENCH_MYSQL_PORT := "3316"
BENCH_MYSQL_NOFSYNC_PORT := "3317"
# mysqld chdirs internally before resolving relative paths, so these must be absolute.
BENCH_MYSQL_DATADIR := justfile_directory() + "/benchmarks/mysql-data"
BENCH_MYSQL_NOFSYNC_DATADIR := justfile_directory() + "/benchmarks/mysql-data-nofsync"

# Initialize (first run only) and start the throwaway benchmark MySQL server
[group('bench')]
bench-mysql-start:
    #!/usr/bin/env bash
    set -euo pipefail
    if [ ! -d {{ BENCH_MYSQL_DATADIR }} ]; then
        {{ MYSQLD }} --no-defaults --initialize-insecure --datadir={{ BENCH_MYSQL_DATADIR }}
    fi
    {{ MYSQLD }} --no-defaults --datadir={{ BENCH_MYSQL_DATADIR }} --port={{ BENCH_MYSQL_PORT }} \
        --socket={{ BENCH_MYSQL_DATADIR }}/mysql.sock --pid-file={{ BENCH_MYSQL_DATADIR }}/mysql.pid \
        > {{ BENCH_MYSQL_DATADIR }}/mysqld.log 2>&1 &
    disown
    for _ in $(seq 1 30); do
        {{ MYSQLADMIN }} -P{{ BENCH_MYSQL_PORT }} --protocol=tcp -h127.0.0.1 -uroot ping &>/dev/null && exit 0
        sleep 1
    done
    echo "mysqld did not become ready, see {{ BENCH_MYSQL_DATADIR }}/mysqld.log" >&2
    exit 1

# Shut down the throwaway benchmark MySQL server
[group('bench')]
bench-mysql-stop:
    {{ MYSQLADMIN }} -P{{ BENCH_MYSQL_PORT }} --protocol=tcp -h127.0.0.1 -uroot shutdown 2>/dev/null || true

# Initialize (first run only) and start the no-fsync throwaway MySQL server.
# `--skip-log-bin --innodb_flush_log_at_trx_commit=0 --sync_binlog=0` removes
# the per-commit fsyncs so engine work can be compared apples-to-apples
# against in-memory fsdb (the `mysql-nofsync` bench target).
[group('bench')]
bench-mysql-start-nofsync:
    #!/usr/bin/env bash
    set -euo pipefail
    if [ ! -d {{ BENCH_MYSQL_NOFSYNC_DATADIR }} ]; then
        {{ MYSQLD }} --no-defaults --initialize-insecure --datadir={{ BENCH_MYSQL_NOFSYNC_DATADIR }}
    fi
    {{ MYSQLD }} --no-defaults --datadir={{ BENCH_MYSQL_NOFSYNC_DATADIR }} --port={{ BENCH_MYSQL_NOFSYNC_PORT }} \
        --socket={{ BENCH_MYSQL_NOFSYNC_DATADIR }}/mysql.sock --pid-file={{ BENCH_MYSQL_NOFSYNC_DATADIR }}/mysql.pid \
        --skip-log-bin --innodb_flush_log_at_trx_commit=0 --sync_binlog=0 \
        > {{ BENCH_MYSQL_NOFSYNC_DATADIR }}/mysqld.log 2>&1 &
    disown
    for _ in $(seq 1 30); do
        {{ MYSQLADMIN }} -P{{ BENCH_MYSQL_NOFSYNC_PORT }} --protocol=tcp -h127.0.0.1 -uroot ping &>/dev/null && exit 0
        sleep 1
    done
    echo "mysqld (no-fsync) did not become ready, see {{ BENCH_MYSQL_NOFSYNC_DATADIR }}/mysqld.log" >&2
    exit 1

# Shut down the no-fsync throwaway MySQL server
[group('bench')]
bench-mysql-stop-nofsync:
    {{ MYSQLADMIN }} -P{{ BENCH_MYSQL_NOFSYNC_PORT }} --protocol=tcp -h127.0.0.1 -uroot shutdown 2>/dev/null || true

# Build fsdb (Release) and run the benchmark suite; shared by bench/bench-quick.
# fsdb itself is no longer started here — ServerBenchmarks restarts it per
# benchmark case (see the module comment there for why) — this just builds
# it once and hands the binary path down via FSDB_BENCH_BIN. The benchmark
# host is run with `dotnet exec` on a prebuilt Release binary rather than
# `dotnet run`, because `dotnet run` sets DOTNET_MODIFIABLE_ASSEMBLIES=debug
# for hot reload, which made BenchmarkDotNet's own [Host] line report DEBUG
# even though the binary was genuinely built Release.
[group('bench')]
[private]
_bench-run *ARGS: bench-mysql-start
    #!/usr/bin/env bash
    set -euo pipefail
    if {{ MYSQL }} --protocol=tcp -h127.0.0.1 -P{{ PORT }} -uroot -e 'SELECT 1' &>/dev/null; then
        echo "error: something is already listening on port {{ PORT }} — stop it first, benchmarking against a shared server would corrupt both" >&2
        exit 1
    fi
    trap 'just bench-mysql-stop' EXIT
    dotnet build src/Fsdb -c Release -v q
    dotnet build benchmarks/Fsdb.Benchmarks -c Release -v q
    export FSDB_BENCH_BIN="$(pwd)/src/Fsdb/bin/Release/net10.0/Fsdb.dll"
    dotnet exec benchmarks/Fsdb.Benchmarks/bin/Release/net10.0/Fsdb.Benchmarks.dll {{ ARGS }}

# As `_bench-run`, but with both mysqld variants up and the four-target
# durability-matched set selected (`FSDB_BENCH_TARGETS=durable`).
[group('bench')]
[private]
_bench-durable-run *ARGS: bench-mysql-start bench-mysql-start-nofsync
    #!/usr/bin/env bash
    set -euo pipefail
    if {{ MYSQL }} --protocol=tcp -h127.0.0.1 -P{{ PORT }} -uroot -e 'SELECT 1' &>/dev/null; then
        echo "error: something is already listening on port {{ PORT }} — stop it first, benchmarking against a shared server would corrupt both" >&2
        exit 1
    fi
    trap 'just bench-mysql-stop; just bench-mysql-stop-nofsync' EXIT
    dotnet build src/Fsdb -c Release -v q
    dotnet build benchmarks/Fsdb.Benchmarks -c Release -v q
    export FSDB_BENCH_BIN="$(pwd)/src/Fsdb/bin/Release/net10.0/Fsdb.dll"
    export FSDB_BENCH_TARGETS=durable
    dotnet exec benchmarks/Fsdb.Benchmarks/bin/Release/net10.0/Fsdb.Benchmarks.dll {{ ARGS }}

# Run the full benchmark suite (fsdb vs MySQL 8.4); results land in benchmarks/results/<git-sha>.md
[group('bench')]
bench:
    @just _bench-run
    @mkdir -p benchmarks/results
    @just _bench-header > "benchmarks/results/$(git rev-parse --short HEAD).md"
    @cat BenchmarkDotNet.Artifacts/results/Fsdb.Benchmarks.ServerBenchmarks.ServerBenchmarks-report-github.md >> "benchmarks/results/$(git rev-parse --short HEAD).md"
    @rm -rf BenchmarkDotNet.Artifacts
    @echo "results: benchmarks/results/$(git rev-parse --short HEAD).md"

# Durability-matched latency: fsdb in-memory and --data-dir (WAL) vs MySQL
# durable and no-fsync. Reclassifies the "fsdb beats MySQL on writes" number
# by matching what each engine actually pays for.
[group('bench')]
bench-durable:
    @just _bench-durable-run
    @mkdir -p benchmarks/results
    @just _bench-header > "benchmarks/results/$(git rev-parse --short HEAD)-durable.md"
    @cat BenchmarkDotNet.Artifacts/results/Fsdb.Benchmarks.ServerBenchmarks.ServerBenchmarks-report-github.md >> "benchmarks/results/$(git rev-parse --short HEAD)-durable.md"
    @rm -rf BenchmarkDotNet.Artifacts
    @echo "results: benchmarks/results/$(git rev-parse --short HEAD)-durable.md"

# Latency suite at 100k users / 500k orders so O(n) vs O(log n) scaling stops
# hiding at the default 10k/50k (seeding per fsdb case dominates the runtime).
[group('bench')]
bench-scale:
    @FSDB_BENCH_USERS=100000 FSDB_BENCH_ORDERS=500000 just _bench-run
    @mkdir -p benchmarks/results
    @just _bench-header > "benchmarks/results/$(git rev-parse --short HEAD)-scale.md"
    @cat BenchmarkDotNet.Artifacts/results/Fsdb.Benchmarks.ServerBenchmarks.ServerBenchmarks-report-github.md >> "benchmarks/results/$(git rev-parse --short HEAD)-scale.md"
    @rm -rf BenchmarkDotNet.Artifacts
    @echo "results: benchmarks/results/$(git rev-parse --short HEAD)-scale.md"

# Environment/provenance header prepended to each results file
[group('bench')]
[private]
_bench-header:
    #!/usr/bin/env bash
    set -euo pipefail
    echo "<!--"
    echo "sha: $(git rev-parse --short HEAD)"
    echo "date: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo "os: $(uname -srm)"
    echo "dotnet: $(dotnet --version)"
    echo "fsdb server mode: in-memory (no --data-dir, no WAL/fsync)"
    echo "-->"
    echo

# Same as `bench`, but with BenchmarkDotNet's ShortRun job for fast local iteration
[group('bench')]
bench-quick:
    @just _bench-run --quick
    @rm -rf BenchmarkDotNet.Artifacts

# N-writer throughput under concurrency, fsdb vs MySQL (ops/sec, not latency).
# Complements `bench`: the latency suite is single-connection and cannot see
# fsdb's per-database write gate serialize writers.
[group('bench')]
bench-load:
    @mkdir -p benchmarks/results
    @just _bench-run --load
    @just _bench-header > "benchmarks/results/$(git rev-parse --short HEAD)-load.md"
    @cat benchmarks/load-report.md >> "benchmarks/results/$(git rev-parse --short HEAD)-load.md"
    @rm -f benchmarks/load-report.md
    @echo "results: benchmarks/results/$(git rev-parse --short HEAD)-load.md"
