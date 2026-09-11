# fsdb — MySQL-compatible database server in F#

set positional-arguments

# MySQL 8.4 client/server tools — resolved from PATH (e.g. homebrew's
# `/opt/homebrew/opt/mysql@8.4/bin`), not hardcoded machine paths.
MYSQL := "mysql"
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

# Run the LlmSearch example (pass --dry-run to stub the LLM HTTP calls)
[group('server')]
example *ARGS:
    dotnet run --project examples/LlmSearch -- {{ ARGS }}

# Run ReceiptPipeline with offline fixtures or PDF paths and endpoint settings.
[group('server')]
receipts *ARGS:
    dotnet run --project examples/ReceiptPipeline -- {{ ARGS }}

# Quick liveness probe against a running server
[group('server')]
smoke port=PORT:
    {{ MYSQL }} --protocol=tcp -h127.0.0.1 -P{{ port }} -uroot -e 'SELECT 1; SELECT @@version;'

# Run pinned external application compatibility probes in Docker.
[group('qa')]
smoke-apps *TARGETS:
    #!/usr/bin/env bash
    dotnet fsi --nologo --readline- --exec smoke/run.fsx -- "$@"

# Build the external smoke images without running their database probes.
[group('qa')]
smoke-apps-build *TARGETS:
    #!/usr/bin/env bash
    dotnet fsi --nologo --readline- --exec smoke/run.fsx -- --build-only "$@"

# === QA ===

# Run the Expecto test suite, passing any arguments through to Expecto
[group('qa')]
test *ARGS:
    #!/usr/bin/env bash
    dotnet run --project tests/Fsdb.Tests -- "$@"

# Build + tests
[group('qa')]
check: build test

# Run the suite without the interactive spinner and retain per-test timings.
[group('qa')]
test-report *ARGS:
    #!/usr/bin/env bash
    set -euo pipefail
    mkdir -p test-results
    dotnet run --project tests/Fsdb.Tests -- \
        --no-spinner \
        --junit-summary test-results/fsdb.xml \
        "$@"

# Large-packet and snapshot cases retain several full-size buffers, so the
# guard stays above their expected peak while still catching unbounded growth.
# Stress the suite with a 6 GiB memory guard.
[group('qa')]
stress minutes="1" *ARGS:
    #!/usr/bin/env bash
    set -euo pipefail
    minutes="$1"
    shift
    dotnet run --project tests/Fsdb.Tests -- \
        --stress "$minutes" \
        --stress-memory-limit 6144 \
        --no-spinner \
        "$@"

# Expecto has no built-in coverage, so Coverlet instruments the Fsdb.dll loaded
# by the test assembly and writes branch data to coverage/coverage.cobertura.xml.
# Measure full-suite branch coverage with the repository-pinned Coverlet tool.
[group('qa')]
coverage:
    #!/usr/bin/env bash
    set -euo pipefail
    dotnet tool restore
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
    export FSDB_COVERAGE=1
    dotnet tool run coverlet "tests/Fsdb.Tests/bin/Debug/net10.0/Fsdb.Tests.dll" \
        -t "$dotnet_bin" \
        -a "tests/Fsdb.Tests/bin/Debug/net10.0/Fsdb.Tests.dll" \
        --include "[Fsdb]*" \
        -f cobertura \
        -o coverage/coverage \
        --threshold 65 \
        --threshold-type branch \
        --threshold-stat total

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

# Install fsdb globally as a single self-contained binary (no .NET needed)
[group('install')]
install dest="~/.local/bin":
    dotnet publish src/Fsdb -c Release -o src/Fsdb/bin/dist -p:PublishSingleFile=true --self-contained true -v q
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

MYSQLD := "mysqld"
MYSQLADMIN := "mysqladmin"
# mysqld chdirs internally before resolving relative paths, so these must be absolute.
BENCH_MYSQL_DATADIR := justfile_directory() + "/benchmarks/mysql-data"
BENCH_MYSQL_NOFSYNC_DATADIR := justfile_directory() + "/benchmarks/mysql-data-nofsync"

# Initialize (first run only) and start the throwaway benchmark MySQL server
[group('bench')]
bench-mysql-start:
    #!/usr/bin/env bash
    set -euo pipefail
    mysql_port="${FSDB_BENCH_MYSQL_PORT:-3316}"
    if [ ! -d {{ BENCH_MYSQL_DATADIR }} ]; then
        {{ MYSQLD }} --no-defaults --initialize-insecure --datadir={{ BENCH_MYSQL_DATADIR }}
    fi
    {{ MYSQLD }} --no-defaults --datadir={{ BENCH_MYSQL_DATADIR }} --port="$mysql_port" \
        --socket={{ BENCH_MYSQL_DATADIR }}/mysql.sock --pid-file={{ BENCH_MYSQL_DATADIR }}/mysql.pid \
        > {{ BENCH_MYSQL_DATADIR }}/mysqld.log 2>&1 &
    disown
    for _ in $(seq 1 30); do
        {{ MYSQLADMIN }} -P"$mysql_port" --protocol=tcp -h127.0.0.1 -uroot ping &>/dev/null && exit 0
        sleep 1
    done
    echo "mysqld did not become ready, see {{ BENCH_MYSQL_DATADIR }}/mysqld.log" >&2
    exit 1

# Shut down the throwaway benchmark MySQL server
[group('bench')]
bench-mysql-stop:
    {{ MYSQLADMIN }} -P"${FSDB_BENCH_MYSQL_PORT:-3316}" --protocol=tcp -h127.0.0.1 -uroot shutdown 2>/dev/null || true

# These settings remove commit-time fsync so the target matches in-memory fsdb
# when the campaign is explicitly measuring non-durable engine work.
# Start a no-fsync MySQL target for matched in-memory engine comparisons.
[group('bench')]
bench-mysql-start-nofsync:
    #!/usr/bin/env bash
    set -euo pipefail
    mysql_port="${FSDB_BENCH_MYSQL_NOFSYNC_PORT:-3317}"
    if [ ! -d {{ BENCH_MYSQL_NOFSYNC_DATADIR }} ]; then
        {{ MYSQLD }} --no-defaults --initialize-insecure --datadir={{ BENCH_MYSQL_NOFSYNC_DATADIR }}
    fi
    {{ MYSQLD }} --no-defaults --datadir={{ BENCH_MYSQL_NOFSYNC_DATADIR }} --port="$mysql_port" \
        --socket={{ BENCH_MYSQL_NOFSYNC_DATADIR }}/mysql.sock --pid-file={{ BENCH_MYSQL_NOFSYNC_DATADIR }}/mysql.pid \
        --skip-log-bin --innodb_flush_log_at_trx_commit=0 --sync_binlog=0 \
        > {{ BENCH_MYSQL_NOFSYNC_DATADIR }}/mysqld.log 2>&1 &
    disown
    for _ in $(seq 1 30); do
        {{ MYSQLADMIN }} -P"$mysql_port" --protocol=tcp -h127.0.0.1 -uroot ping &>/dev/null && exit 0
        sleep 1
    done
    echo "mysqld (no-fsync) did not become ready, see {{ BENCH_MYSQL_NOFSYNC_DATADIR }}/mysqld.log" >&2
    exit 1

# Shut down the no-fsync throwaway MySQL server
[group('bench')]
bench-mysql-stop-nofsync:
    {{ MYSQLADMIN }} -P"${FSDB_BENCH_MYSQL_NOFSYNC_PORT:-3317}" --protocol=tcp -h127.0.0.1 -uroot shutdown 2>/dev/null || true

# Each case restarts fsdb so a timed-out query cannot poison later results.
# `dotnet exec` also avoids the hot-reload environment that makes a Release
# binary appear as DEBUG to BenchmarkDotNet.
# Build Release binaries and run isolated per-case fsdb benchmark processes.
[group('bench')]
[private]
_bench-run *ARGS: bench-mysql-start
    #!/usr/bin/env bash
    set -euo pipefail
    fsdb_bench_port="${FSDB_BENCH_PORT:-{{ PORT }}}"
    if {{ MYSQL }} --protocol=tcp -h127.0.0.1 -P"$fsdb_bench_port" -uroot -e 'SELECT 1' &>/dev/null; then
        echo "error: something is already listening on port $fsdb_bench_port — stop it first, benchmarking against a shared server would corrupt both" >&2
        exit 1
    fi
    trap 'just bench-mysql-stop' EXIT
    dotnet build src/Fsdb -c Release -v q
    dotnet build benchmarks/Fsdb.Benchmarks -c Release -v q
    export FSDB_BENCH_BIN="$(pwd)/src/Fsdb/bin/Release/net10.0/Fsdb.dll"
    dotnet exec benchmarks/Fsdb.Benchmarks/bin/Release/net10.0/Fsdb.Benchmarks.dll {{ ARGS }}

# The matrix pairs WAL-backed fsdb with durable MySQL and in-memory fsdb with
# MySQL configured without commit-time fsync.
# Run the four-target durability matrix.
[group('bench')]
[private]
_bench-durable-run *ARGS: bench-mysql-start bench-mysql-start-nofsync
    #!/usr/bin/env bash
    set -euo pipefail
    fsdb_bench_port="${FSDB_BENCH_PORT:-{{ PORT }}}"
    if {{ MYSQL }} --protocol=tcp -h127.0.0.1 -P"$fsdb_bench_port" -uroot -e 'SELECT 1' &>/dev/null; then
        echo "error: something is already listening on port $fsdb_bench_port — stop it first, benchmarking against a shared server would corrupt both" >&2
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
    @if [ -f BenchmarkDotNet.Artifacts/results/Fsdb.Benchmarks.ServerBenchmarks.ConnectBenchmarks-report-github.md ]; then cat BenchmarkDotNet.Artifacts/results/Fsdb.Benchmarks.ServerBenchmarks.ConnectBenchmarks-report-github.md >> "benchmarks/results/$(git rev-parse --short HEAD).md"; fi
    @rm -rf BenchmarkDotNet.Artifacts
    @echo "results: benchmarks/results/$(git rev-parse --short HEAD).md"

# Recent SQL features without rerunning the established core/auth matrix.
[group('bench')]
bench-features:
    @FSDB_BENCH_CATEGORIES=Feature just _bench-run
    @mkdir -p benchmarks/results
    @just _bench-header > "benchmarks/results/$(git rev-parse --short HEAD)-features.md"
    @cat BenchmarkDotNet.Artifacts/results/Fsdb.Benchmarks.ServerBenchmarks.ServerBenchmarks-report-github.md >> "benchmarks/results/$(git rev-parse --short HEAD)-features.md"
    @rm -rf BenchmarkDotNet.Artifacts
    @echo "results: benchmarks/results/$(git rev-parse --short HEAD)-features.md"

# Read-only cases cannot observe the storage mode, so this recipe restricts the
# four-target matrix to writes tagged `Durability`.
# Compare WAL/durable and in-memory/no-fsync write latency pairs.
[group('bench')]
bench-durable:
    @FSDB_BENCH_CATEGORIES=Durability just _bench-durable-run
    @mkdir -p benchmarks/results
    @just _bench-header "fsdb in-memory/WAL; MySQL durable/no-fsync" > "benchmarks/results/$(git rev-parse --short HEAD)-durable.md"
    @cat BenchmarkDotNet.Artifacts/results/Fsdb.Benchmarks.ServerBenchmarks.ServerBenchmarks-report-github.md >> "benchmarks/results/$(git rev-parse --short HEAD)-durable.md"
    @if [ -f BenchmarkDotNet.Artifacts/results/Fsdb.Benchmarks.ServerBenchmarks.ConnectBenchmarks-report-github.md ]; then cat BenchmarkDotNet.Artifacts/results/Fsdb.Benchmarks.ServerBenchmarks.ConnectBenchmarks-report-github.md >> "benchmarks/results/$(git rev-parse --short HEAD)-durable.md"; fi
    @rm -rf BenchmarkDotNet.Artifacts
    @echo "results: benchmarks/results/$(git rev-parse --short HEAD)-durable.md"

# Larger cardinalities expose O(n) versus O(log n) slopes that the default
# corpus can hide; per-case seeding dominates the resulting runtime.
# Run scale-sensitive latency cases at 100k users and 500k orders.
[group('bench')]
bench-scale:
    @FSDB_BENCH_USERS=100000 FSDB_BENCH_ORDERS=500000 FSDB_BENCH_ARTICLES=100000 FSDB_BENCH_CATEGORIES=Scale just _bench-run
    @mkdir -p benchmarks/results
    @just _bench-header "in-memory fsdb; durable MySQL" 100000 500000 100000 > "benchmarks/results/$(git rev-parse --short HEAD)-scale.md"
    @cat BenchmarkDotNet.Artifacts/results/Fsdb.Benchmarks.ServerBenchmarks.ServerBenchmarks-report-github.md >> "benchmarks/results/$(git rev-parse --short HEAD)-scale.md"
    @rm -rf BenchmarkDotNet.Artifacts
    @echo "results: benchmarks/results/$(git rev-parse --short HEAD)-scale.md"

# Environment/provenance header prepended to each results file
[group('bench')]
[private]
_bench-header mode="in-memory fsdb; durable MySQL" users="10000" orders="50000" articles="10000":
    #!/usr/bin/env bash
    set -euo pipefail
    bench_users="${FSDB_BENCH_USERS:-{{ users }}}"
    bench_orders="${FSDB_BENCH_ORDERS:-{{ orders }}}"
    bench_articles="${FSDB_BENCH_ARTICLES:-{{ articles }}}"
    echo "<!--"
    echo "sha: $(git rev-parse --short HEAD)"
    echo "date: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo "os: $(uname -srm)"
    echo "dotnet: $(dotnet --version)"
    echo "mysql: $({{ MYSQL }} --version)"
    echo "targets: {{ mode }}"
    echo "dataset: $bench_users users, $bench_orders orders, $bench_articles articles"
    echo "-->"
    echo

# Same as `bench`, but with BenchmarkDotNet's ShortRun job for fast local iteration
[group('bench')]
bench-quick:
    @just _bench-run --quick
    @mkdir -p benchmarks/results
    @just _bench-header > "benchmarks/results/$(git rev-parse --short HEAD)-quick.md"
    @cat BenchmarkDotNet.Artifacts/results/Fsdb.Benchmarks.ServerBenchmarks.ServerBenchmarks-report-github.md >> "benchmarks/results/$(git rev-parse --short HEAD)-quick.md"
    @if [ -f BenchmarkDotNet.Artifacts/results/Fsdb.Benchmarks.ServerBenchmarks.ConnectBenchmarks-report-github.md ]; then cat BenchmarkDotNet.Artifacts/results/Fsdb.Benchmarks.ServerBenchmarks.ConnectBenchmarks-report-github.md >> "benchmarks/results/$(git rev-parse --short HEAD)-quick.md"; fi
    @rm -rf BenchmarkDotNet.Artifacts
    @echo "results: benchmarks/results/$(git rev-parse --short HEAD)-quick.md"

# The ordinary latency suite is single-connection and cannot expose optimistic
# publication behavior under multiple writers.
# Measure concurrent fsdb/MySQL throughput in operations per second.
[group('bench')]
bench-load:
    @mkdir -p benchmarks/results
    @just _bench-run --load
    @just _bench-header > "benchmarks/results/$(git rev-parse --short HEAD)-load.md"
    @cat benchmarks/load-report.md >> "benchmarks/results/$(git rev-parse --short HEAD)-load.md"
    @rm -f benchmarks/load-report.md
    @echo "results: benchmarks/results/$(git rev-parse --short HEAD)-load.md"

# Worker-scaling throughput with longer samples than the default load check.
[group('bench')]
bench-load-scale:
    @mkdir -p benchmarks/results
    @FSDB_LOAD_WORKERS=1,2,4,8,16 FSDB_LOAD_TRIALS=3 FSDB_LOAD_WARMUP=2 FSDB_LOAD_SECONDS=10 just _bench-run --load
    @just _bench-header > "benchmarks/results/$(git rev-parse --short HEAD)-load-scale.md"
    @cat benchmarks/load-report.md >> "benchmarks/results/$(git rev-parse --short HEAD)-load-scale.md"
    @rm -f benchmarks/load-report.md
    @echo "results: benchmarks/results/$(git rev-parse --short HEAD)-load-scale.md"

# Run latency, durability, data-size, and worker-scaling comparisons.
[group('bench')]
bench-comprehensive: bench bench-durable bench-scale bench-load-scale
