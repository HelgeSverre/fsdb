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

# Run server (--port, --listen, etc.)
[group('server')]
run *ARGS:
    dotnet run --project src/Fsdb -- {{ ARGS }}

# Open mysql client shell
[group('server')]
client port=PORT:
    {{ MYSQL }} --protocol=tcp -h127.0.0.1 -P{{ port }} -uroot

# Run LlmSearch example
[group('server')]
example *ARGS:
    dotnet run --project examples/LlmSearch -- {{ ARGS }}

# Run ReceiptPipeline example
[group('server')]
receipts *ARGS:
    dotnet run --project examples/ReceiptPipeline -- {{ ARGS }}

# Liveness probe
[group('server')]
smoke port=PORT:
    {{ MYSQL }} --protocol=tcp -h127.0.0.1 -P{{ port }} -uroot -e 'SELECT 1; SELECT @@version;'

# Run smoke tests in Docker
[group('qa')]
smoke-apps *TARGETS:
    #!/usr/bin/env bash
    dotnet fsi --nologo --readline- --exec smoke/run.fsx -- "$@"

# Build smoke images only
[group('qa')]
smoke-apps-build *TARGETS:
    #!/usr/bin/env bash
    dotnet fsi --nologo --readline- --exec smoke/run.fsx -- --build-only "$@"

# === QA ===

# Run test suite
[group('qa')]
test *ARGS:
    #!/usr/bin/env bash
    dotnet run --project tests/Fsdb.Tests -- "$@"

# Build + tests
[group('qa')]
check: build test

# Run tests with timings
[group('qa')]
test-report *ARGS:
    #!/usr/bin/env bash
    set -euo pipefail
    mkdir -p test-results
    dotnet run --project tests/Fsdb.Tests -- \
        --no-spinner \
        --junit-summary test-results/fsdb.xml \
        "$@"

# Stress test (6 GiB memory limit)
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

# Measure branch coverage
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

# Build solution
[group('build')]
build:
    dotnet build

# Clean build artifacts
[group('build')]
clean:
    dotnet clean -v q
    rm -rf src/Fsdb/bin src/Fsdb/obj tests/Fsdb.Tests/bin tests/Fsdb.Tests/obj

# === Install ===

# Install standalone binary
[group('install')]
install dest="~/.local/bin":
    dotnet publish src/Fsdb -c Release -o src/Fsdb/bin/dist -p:PublishSingleFile=true --self-contained true -v q
    mkdir -p {{ dest }}
    install -m 0755 src/Fsdb/bin/dist/Fsdb {{ dest }}/fsdb
    @echo "Installed {{ dest }}/fsdb — try: fsdb --help"

# Uninstall binary
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

# Start benchmark MySQL
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

# Stop benchmark MySQL
[group('bench')]
bench-mysql-stop:
    {{ MYSQLADMIN }} -P"${FSDB_BENCH_MYSQL_PORT:-3316}" --protocol=tcp -h127.0.0.1 -uroot shutdown 2>/dev/null || true

# Start MySQL no-fsync
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

# Stop MySQL no-fsync
[group('bench')]
bench-mysql-stop-nofsync:
    {{ MYSQLADMIN }} -P"${FSDB_BENCH_MYSQL_NOFSYNC_PORT:-3317}" --protocol=tcp -h127.0.0.1 -uroot shutdown 2>/dev/null || true

# Run isolated fsdb benchmarks
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

# Run durability matrix
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

# Full benchmark suite
[group('bench')]
bench:
    @just _bench-run
    @mkdir -p benchmarks/results
    @just _bench-header > "benchmarks/results/$(git rev-parse --short HEAD).md"
    @cat BenchmarkDotNet.Artifacts/results/Fsdb.Benchmarks.ServerBenchmarks.ServerBenchmarks-report-github.md >> "benchmarks/results/$(git rev-parse --short HEAD).md"
    @if [ -f BenchmarkDotNet.Artifacts/results/Fsdb.Benchmarks.ServerBenchmarks.ConnectBenchmarks-report-github.md ]; then cat BenchmarkDotNet.Artifacts/results/Fsdb.Benchmarks.ServerBenchmarks.ConnectBenchmarks-report-github.md >> "benchmarks/results/$(git rev-parse --short HEAD).md"; fi
    @rm -rf BenchmarkDotNet.Artifacts
    @echo "results: benchmarks/results/$(git rev-parse --short HEAD).md"

# Benchmark features only
[group('bench')]
bench-features:
    @FSDB_BENCH_CATEGORIES=Feature just _bench-run
    @mkdir -p benchmarks/results
    @just _bench-header > "benchmarks/results/$(git rev-parse --short HEAD)-features.md"
    @cat BenchmarkDotNet.Artifacts/results/Fsdb.Benchmarks.ServerBenchmarks.ServerBenchmarks-report-github.md >> "benchmarks/results/$(git rev-parse --short HEAD)-features.md"
    @rm -rf BenchmarkDotNet.Artifacts
    @echo "results: benchmarks/results/$(git rev-parse --short HEAD)-features.md"

# Benchmark durability
[group('bench')]
bench-durable:
    @FSDB_BENCH_CATEGORIES=Durability just _bench-durable-run
    @mkdir -p benchmarks/results
    @just _bench-header "fsdb in-memory/WAL; MySQL durable/no-fsync" > "benchmarks/results/$(git rev-parse --short HEAD)-durable.md"
    @cat BenchmarkDotNet.Artifacts/results/Fsdb.Benchmarks.ServerBenchmarks.ServerBenchmarks-report-github.md >> "benchmarks/results/$(git rev-parse --short HEAD)-durable.md"
    @if [ -f BenchmarkDotNet.Artifacts/results/Fsdb.Benchmarks.ServerBenchmarks.ConnectBenchmarks-report-github.md ]; then cat BenchmarkDotNet.Artifacts/results/Fsdb.Benchmarks.ServerBenchmarks.ConnectBenchmarks-report-github.md >> "benchmarks/results/$(git rev-parse --short HEAD)-durable.md"; fi
    @rm -rf BenchmarkDotNet.Artifacts
    @echo "results: benchmarks/results/$(git rev-parse --short HEAD)-durable.md"

# Benchmark at scale
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

# Quick benchmark
[group('bench')]
bench-quick:
    @just _bench-run --quick
    @mkdir -p benchmarks/results
    @just _bench-header > "benchmarks/results/$(git rev-parse --short HEAD)-quick.md"
    @cat BenchmarkDotNet.Artifacts/results/Fsdb.Benchmarks.ServerBenchmarks.ServerBenchmarks-report-github.md >> "benchmarks/results/$(git rev-parse --short HEAD)-quick.md"
    @if [ -f BenchmarkDotNet.Artifacts/results/Fsdb.Benchmarks.ServerBenchmarks.ConnectBenchmarks-report-github.md ]; then cat BenchmarkDotNet.Artifacts/results/Fsdb.Benchmarks.ServerBenchmarks.ConnectBenchmarks-report-github.md >> "benchmarks/results/$(git rev-parse --short HEAD)-quick.md"; fi
    @rm -rf BenchmarkDotNet.Artifacts
    @echo "results: benchmarks/results/$(git rev-parse --short HEAD)-quick.md"

# Benchmark throughput
[group('bench')]
bench-load:
    @mkdir -p benchmarks/results
    @just _bench-run --load
    @just _bench-header > "benchmarks/results/$(git rev-parse --short HEAD)-load.md"
    @cat benchmarks/load-report.md >> "benchmarks/results/$(git rev-parse --short HEAD)-load.md"
    @rm -f benchmarks/load-report.md
    @echo "results: benchmarks/results/$(git rev-parse --short HEAD)-load.md"

# Benchmark load scaling
[group('bench')]
bench-load-scale:
    @mkdir -p benchmarks/results
    @FSDB_LOAD_WORKERS=1,2,4,8,16 FSDB_LOAD_TRIALS=3 FSDB_LOAD_WARMUP=2 FSDB_LOAD_SECONDS=10 just _bench-run --load
    @just _bench-header > "benchmarks/results/$(git rev-parse --short HEAD)-load-scale.md"
    @cat benchmarks/load-report.md >> "benchmarks/results/$(git rev-parse --short HEAD)-load-scale.md"
    @rm -f benchmarks/load-report.md
    @echo "results: benchmarks/results/$(git rev-parse --short HEAD)-load-scale.md"

# Full benchmark matrix
[group('bench')]
bench-comprehensive: bench bench-durable bench-scale bench-load-scale
