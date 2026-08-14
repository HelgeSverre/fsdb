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
