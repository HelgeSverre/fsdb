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

# === Bench ===
# fsdb vs a native MySQL 8.4, run ad hoc (no brew services / launchd).

MYSQLD := "/opt/homebrew/opt/mysql@8.4/bin/mysqld"
MYSQLADMIN := "/opt/homebrew/opt/mysql@8.4/bin/mysqladmin"
BENCH_MYSQL_PORT := "3316"
# mysqld chdirs internally before resolving relative paths, so this must be absolute.
BENCH_MYSQL_DATADIR := justfile_directory() + "/benchmarks/mysql-data"

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

# Build + run fsdb (Release) and the benchmark suite against it; shared by bench/bench-quick
[group('bench')]
[private]
_bench-run *ARGS: bench-mysql-start
    #!/usr/bin/env bash
    set -euo pipefail
    if {{ MYSQL }} --protocol=tcp -h127.0.0.1 -P{{ PORT }} -uroot -e 'SELECT 1' &>/dev/null; then
        echo "error: something is already listening on port {{ PORT }} — stop it first, benchmarking against a shared server would corrupt both" >&2
        exit 1
    fi
    dotnet build src/Fsdb -c Release -v q
    dotnet run -c Release --no-build --project src/Fsdb -- --port {{ PORT }} &
    FSDB_PID=$!
    trap 'kill $FSDB_PID 2>/dev/null || true; just bench-mysql-stop' EXIT
    for _ in $(seq 1 30); do
        {{ MYSQL }} --protocol=tcp -h127.0.0.1 -P{{ PORT }} -uroot -e 'SELECT 1' &>/dev/null && break
        sleep 1
    done
    dotnet run -c Release --project benchmarks/Fsdb.Benchmarks -- {{ ARGS }}

# Run the full benchmark suite (fsdb vs MySQL 8.4); results land in benchmarks/results/<git-sha>.md
[group('bench')]
bench:
    @just _bench-run
    @mkdir -p benchmarks/results
    @cp BenchmarkDotNet.Artifacts/results/Fsdb.Benchmarks.ServerBenchmarks.ServerBenchmarks-report-github.md "benchmarks/results/$(git rev-parse --short HEAD).md"
    @rm -rf BenchmarkDotNet.Artifacts
    @echo "results: benchmarks/results/$(git rev-parse --short HEAD).md"

# Same as `bench`, but with BenchmarkDotNet's ShortRun job for fast local iteration
[group('bench')]
bench-quick:
    @just _bench-run --quick
    @rm -rf BenchmarkDotNet.Artifacts
