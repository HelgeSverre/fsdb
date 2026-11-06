"""Profile the torture transfer workload with fsdb in a separate process.

Build Fsdb.Cli in Release first, then run with `uv run --with pymysql python`.
The server and all databases live in a disposable temporary directory.
"""

import argparse
import concurrent.futures
import json
import pathlib
import socket
import subprocess
import tempfile
import threading
import time

import pymysql

ROOT = pathlib.Path(__file__).resolve().parents[2]
CLI = ROOT / "src/Fsdb.Cli/bin/Release/net10.0/Fsdb.Cli.dll"
INITIAL_BALANCE = 1_000_000


def connection(port, database=None):
    return pymysql.connect(
        host="127.0.0.1",
        port=port,
        user="root",
        database=database,
        autocommit=True,
        connect_timeout=10,
    )


def wait_for_server(port, process):
    for _ in range(100):
        if process.poll() is not None:
            raise RuntimeError(f"fsdb exited with status {process.returncode}")
        try:
            with connection(port):
                return
        except pymysql.MySQLError:
            time.sleep(0.1)
    raise RuntimeError("fsdb did not become ready")


def process_cpu_seconds(pid):
    raw = subprocess.check_output(
        ["ps", "-o", "time=", "-p", str(pid)], text=True
    ).strip()
    fields = raw.split(":")
    return sum(float(value) * 60**index for index, value in enumerate(reversed(fields)))


def operation(seed, hot_accounts, worker, iteration, operations):
    ordinal = worker * operations + iteration + 1
    source = (seed % hot_accounts + ordinal * 17 % hot_accounts) % hot_accounts + 1
    offset = (seed // 7 + ordinal * 31 % (hot_accounts - 1)) % (hot_accounts - 1) + 1
    target = (source - 1 + offset) % hot_accounts + 1
    amount = 1 + (seed % 97 + ordinal * 43 % 97) % 97
    return ordinal, source, target, amount


def setup(port, name, accounts):
    with connection(port) as admin, admin.cursor() as cursor:
        cursor.execute(f"CREATE DATABASE `{name}`")
    with connection(port, name) as database, database.cursor() as cursor:
        cursor.execute(
            "CREATE TABLE concurrency_accounts "
            "(id INT PRIMARY KEY, balance BIGINT NOT NULL, version BIGINT NOT NULL)"
        )
        cursor.execute(
            "CREATE TABLE concurrency_ledger "
            "(operation_id BIGINT PRIMARY KEY, worker_id INT NOT NULL, "
            "from_account INT NOT NULL, to_account INT NOT NULL, amount BIGINT NOT NULL)"
        )
        cursor.executemany(
            "INSERT INTO concurrency_accounts VALUES (%s,%s,0)",
            [(account, INITIAL_BALANCE) for account in range(1, accounts + 1)],
        )


def worker_run(
    port, name, seed, workers, operations, hot_accounts, rollback_every, worker, barrier
):
    with connection(port, name) as database, database.cursor() as cursor:
        for iteration in range(operations):
            ordinal, source, target, amount = operation(
                seed, hot_accounts, worker, iteration, operations
            )
            cursor.execute("START TRANSACTION")
            barrier.wait(timeout=90)
            first, second = sorted((source, target))
            for account in (first, second):
                delta = -amount if account == source else amount
                cursor.execute(
                    "UPDATE concurrency_accounts "
                    "SET balance=balance+%s, version=version+1 WHERE id=%s",
                    (delta, account),
                )
            cursor.execute(
                "INSERT INTO concurrency_ledger VALUES (%s,%s,%s,%s,%s)",
                (ordinal, worker, source, target, amount),
            )
            cursor.execute(
                "ROLLBACK"
                if rollback_every and ordinal % rollback_every == 0
                else "COMMIT"
            )
            barrier.wait(timeout=90)


def verify(
    port, name, seed, workers, operations, accounts, hot_accounts, rollback_every
):
    balances = [INITIAL_BALANCE] * accounts
    versions = [0] * accounts
    ledger = []
    for worker in range(workers):
        for iteration in range(operations):
            ordinal, source, target, amount = operation(
                seed, hot_accounts, worker, iteration, operations
            )
            if rollback_every and ordinal % rollback_every == 0:
                continue
            balances[source - 1] -= amount
            balances[target - 1] += amount
            versions[source - 1] += 1
            versions[target - 1] += 1
            ledger.append(ordinal)
    with connection(port, name) as database, database.cursor() as cursor:
        cursor.execute(
            "SELECT id,balance,version FROM concurrency_accounts ORDER BY id"
        )
        actual_accounts = cursor.fetchall()
        cursor.execute(
            "SELECT operation_id FROM concurrency_ledger ORDER BY operation_id"
        )
        actual_ledger = [row[0] for row in cursor.fetchall()]
    expected_accounts = [
        (account, balances[account - 1], versions[account - 1])
        for account in range(1, accounts + 1)
    ]
    if actual_accounts != tuple(expected_accounts) or actual_ledger != sorted(ledger):
        raise AssertionError(f"ledger or account state diverged in {name}")
    return len(ledger)


def run_phase(
    port,
    server_pid,
    phase,
    databases,
    workers,
    operations,
    accounts,
    hot_accounts,
    rollback_every,
    seed,
):
    names = [f"profile_{phase}_{hot_accounts}_{index}" for index in range(databases)]
    for name in names:
        setup(port, name, accounts)
    barriers = [threading.Barrier(workers) for _ in names]
    start_cpu = time.process_time()
    start_server_cpu = process_cpu_seconds(server_pid)
    started = time.perf_counter()
    with concurrent.futures.ThreadPoolExecutor(max_workers=databases * workers) as pool:
        futures = [
            pool.submit(
                worker_run,
                port,
                name,
                seed + index * 104729,
                workers,
                operations,
                hot_accounts,
                rollback_every,
                worker,
                barriers[index],
            )
            for index, name in enumerate(names)
            for worker in range(workers)
        ]
        for future in futures:
            future.result()
    wall = time.perf_counter() - started
    client_cpu = time.process_time() - start_cpu
    server_cpu = process_cpu_seconds(server_pid) - start_server_cpu
    commits = [
        verify(
            port,
            name,
            seed + index * 104729,
            workers,
            operations,
            accounts,
            hot_accounts,
            rollback_every,
        )
        for index, name in enumerate(names)
    ]
    return {
        "wall_seconds": round(wall, 3),
        "client_cpu_seconds": round(client_cpu, 3),
        "server_cpu_seconds": round(server_cpu, 3),
        "commits_per_database": commits,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--databases", type=int, default=8)
    parser.add_argument("--workers", type=int, default=16)
    parser.add_argument("--operations", type=int, default=30)
    parser.add_argument("--accounts", type=int, default=64)
    parser.add_argument("--hot-accounts", type=int, nargs="+", default=[8, 64])
    parser.add_argument("--rollback-every", type=int, default=7)
    parser.add_argument("--seed", type=int, default=204)
    args = parser.parse_args()
    if not CLI.exists():
        parser.error(f"build the Release CLI first: {CLI}")
    if args.databases < 1 or args.workers < 1 or args.operations < 1:
        parser.error("databases, workers, and operations must be positive")
    if any(hot < 2 or hot > args.accounts for hot in args.hot_accounts):
        parser.error("hot accounts must be between 2 and accounts")
    with tempfile.TemporaryDirectory(prefix="fsdb-external-multidb-") as directory:
        with socket.socket() as reservation:
            reservation.bind(("127.0.0.1", 0))
            port = reservation.getsockname()[1]
        with (pathlib.Path(directory) / "server.log").open("w") as log:
            process = subprocess.Popen(
                [
                    "dotnet",
                    str(CLI),
                    "--data-dir",
                    str(pathlib.Path(directory) / "data"),
                    "--port",
                    str(port),
                ],
                stdout=log,
                stderr=subprocess.STDOUT,
            )
            try:
                wait_for_server(port, process)
                for phase, hot in enumerate(args.hot_accounts):
                    result = run_phase(
                        port,
                        process.pid,
                        phase,
                        args.databases,
                        args.workers,
                        args.operations,
                        args.accounts,
                        hot,
                        args.rollback_every,
                        args.seed,
                    )
                    print(json.dumps({"hot_accounts": hot, **result}), flush=True)
            finally:
                process.terminate()
                process.wait(timeout=20)


if __name__ == "__main__":
    main()
