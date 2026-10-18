"""Probe MySQL 8.4 secondary-index ordered limited-write wait boundaries."""

import pathlib
import subprocess
import tempfile
import threading
import time

import pymysql


def connect(socket):
    for _ in range(100):
        try:
            return pymysql.connect(unix_socket=socket, user="root", autocommit=True)
        except pymysql.MySQLError:
            time.sleep(0.1)
    raise RuntimeError("MySQL did not become ready")


def main():
    with tempfile.TemporaryDirectory(prefix="fsdb-secondary-frontier-") as directory:
        root = pathlib.Path(directory)
        data = root / "data"
        data.mkdir()
        socket = str(root / "mysql.sock")
        server = owner = None
        with (root / "mysql.log").open("w") as log:
            try:
                subprocess.run(
                    ["mysqld", "--no-defaults", "--initialize-insecure", f"--datadir={data}",
                     "--innodb-redo-log-capacity=64M"],
                    check=True, stdout=log, stderr=subprocess.STDOUT,
                )
                server = subprocess.Popen(
                    ["mysqld", "--no-defaults", f"--datadir={data}", "--skip-networking",
                     f"--socket={socket}", f"--pid-file={root / 'mysql.pid'}", "--mysqlx=0",
                     "--innodb-buffer-pool-size=64M", "--innodb-redo-log-capacity=64M"],
                    stdout=log, stderr=subprocess.STDOUT,
                )
                owner = connect(socket)
                with owner.cursor() as cursor:
                    cursor.execute("SELECT VERSION()")
                    version = cursor.fetchone()[0]
                    assert version.startswith("8.4."), version
                    cursor.execute("CREATE DATABASE probe")
                    cursor.execute("USE probe")
                base_cases = (
                    *[(1, size) for size in (1, 2, 3, 4, 8)],
                    *[(2, size) for size in (2, 3, 4, 5, 8, 16, 32, 64, 128, 256)],
                    *[(3, size) for size in (3, 4, 5, 6, 8)],
                    *[(4, size) for size in (4, 5, 6, 7, 8, 9)],
                )
                cases = [(limit, size, "n>=0", size < 2 * limit, size + 1, "UPDATE")
                         for limit, size in base_cases]
                cases += [(2, 3, "", True, 4, "UPDATE"),
                          (2, 3, "id<=2", False, 4, "UPDATE"),
                          (2, 3, "rank_value<=2", False, 4, "UPDATE"),
                          (2, 3, "id<=2", False, 0, "UPDATE"),
                          (2, 3, "id<=2", False, 0, "DELETE"),
                          (2, 3, "id IN (1,2)", False, 0, "UPDATE"),
                          (2, 3, "id IN (1,2)", False, 0, "DELETE"),
                          (2, 3, "id IN (1,2,4)", True, 0, "UPDATE"),
                          (2, 3, "id IN (1,2,4)", True, 0, "DELETE"),
                          (2, 3, "id NOT IN (4,5)", False, 0, "UPDATE"),
                          (2, 3, "id NOT IN (4,5)", False, 0, "DELETE"),
                          (2, 3, "rank_value IS NOT NULL", True, 0, "UPDATE"),
                          (2, 3, "rank_value IS NOT NULL", True, 0, "DELETE"),
                          (2, 3, "rank_value IS NOT NULL", False, None, "UPDATE"),
                          (2, 3, "rank_value IS NOT NULL", False, None, "DELETE")]
                for limit, size, predicate, should_wait, inserted_rank, verb in cases:
                    waiter = connect(socket)
                    try:
                        with owner.cursor() as cursor:
                            cursor.execute("DROP TABLE IF EXISTS target")
                            cursor.execute("CREATE TABLE target(id INT PRIMARY KEY,rank_value INT,n INT,KEY ix_rank(rank_value))")
                            cursor.executemany("INSERT INTO target VALUES(%s,%s,0)", [(i, i) for i in range(1, size + 1)])
                            cursor.execute("BEGIN")
                            cursor.execute("INSERT INTO target VALUES(%s,%s,0)", (size + 1, inserted_rank))
                        with waiter.cursor() as cursor:
                            cursor.execute("USE probe")
                            cursor.execute("BEGIN")
                        started = threading.Event()
                        result = {}

                        def write():
                            try:
                                with waiter.cursor() as cursor:
                                    started.set()
                                    where = f" WHERE {predicate}" if predicate else ""
                                    command = "UPDATE target SET n=n+1" if verb == "UPDATE" else "DELETE FROM target"
                                    cursor.execute(f"{command}{where} ORDER BY rank_value ASC LIMIT {limit}")
                                    result["affected"] = cursor.rowcount
                                    cursor.execute("COMMIT")
                            except Exception as error:
                                result["error"] = str(error)

                        thread = threading.Thread(target=write, daemon=True)
                        thread.start()
                        assert started.wait(2)
                        thread.join(0.25)
                        waited = thread.is_alive()
                        owner.commit()
                        thread.join(5)
                        assert not thread.is_alive(), (size, result)
                        assert result == {"affected": limit}, (limit, size, result)
                        assert waited == should_wait, (limit, size, predicate, waited)
                        with owner.cursor() as cursor:
                            cursor.execute("SELECT id,n FROM target ORDER BY id")
                            final_rows = cursor.fetchall()
                        selected_ids = ((size + 1, 1) if predicate == "id IN (1,2,4)"
                                        or (predicate == "rank_value IS NOT NULL" and inserted_rank == 0)
                                        else tuple(range(1, limit + 1)))
                        expected_rows = (tuple((i, int(i in selected_ids)) for i in range(1, size + 2))
                                         if verb == "UPDATE" else
                                         tuple((i, 0) for i in range(1, size + 2) if i not in selected_ids))
                        assert final_rows == expected_rows, (limit, size, predicate, final_rows)
                        print(f"MySQL {version}: {verb} limit={limit} visible_rows={size} predicate={predicate or 'none'} inserted_rank={inserted_rank} wait={waited}", flush=True)
                    finally:
                        waiter.close()
                tie_cases = (
                    ("IS NULL", "ASC", None, 0, True, (0, 1)),
                    ("IS NULL", "ASC", None, 4, False, (1, 2)),
                    ("IS NULL", "ASC", 0, 0, False, (1, 2)),
                    ("IS NULL", "ASC", 0, 4, False, (1, 2)),
                    ("IS NULL", "DESC", 0, 0, False, (2, 1)),
                    ("IS NULL", "DESC", 0, 4, False, (2, 1)),
                    ("IS NOT NULL", "ASC", 2, 0, True, (1, 0)),
                    ("IS NOT NULL", "ASC", 2, 4, False, (1, 2)),
                    ("IS NOT NULL", "DESC", 2, 0, False, (3, 2)),
                    ("IS NOT NULL", "DESC", 2, 4, True, (3, 4)),
                )
                for predicate, direction, pending_rank, pending_id, should_wait, selected in tie_cases:
                    for verb in ("UPDATE", "DELETE"):
                        waiter = connect(socket)
                        try:
                            with owner.cursor() as cursor:
                                cursor.execute("DROP TABLE IF EXISTS target_tie")
                                cursor.execute("CREATE TABLE target_tie(id INT PRIMARY KEY,rank_value INT,n INT,KEY ix_rank(rank_value))")
                                if predicate == "IS NULL":
                                    cursor.execute("INSERT INTO target_tie VALUES(1,NULL,0),(2,NULL,0),(3,3,0)")
                                else:
                                    cursor.execute("INSERT INTO target_tie VALUES(1,1,0),(2,2,0),(3,3,0)")
                                cursor.execute("BEGIN")
                                cursor.execute("INSERT INTO target_tie VALUES(%s,%s,0)", (pending_id, pending_rank))
                            with waiter.cursor() as cursor:
                                cursor.execute("USE probe")
                                cursor.execute("BEGIN")
                            started = threading.Event()
                            result = {}

                            def write_tie():
                                try:
                                    with waiter.cursor() as cursor:
                                        started.set()
                                        command = "UPDATE target_tie SET n=n+1" if verb == "UPDATE" else "DELETE FROM target_tie"
                                        cursor.execute(
                                            command + f" WHERE rank_value {predicate} ORDER BY rank_value {direction} LIMIT 2"
                                        )
                                        result["affected"] = cursor.rowcount
                                        cursor.execute("COMMIT")
                                except Exception as error:
                                    result["error"] = str(error)

                            thread = threading.Thread(target=write_tie, daemon=True)
                            thread.start()
                            assert started.wait(2)
                            thread.join(0.25)
                            waited = thread.is_alive()
                            owner.commit()
                            thread.join(5)
                            assert not thread.is_alive(), (predicate, direction, pending_id, result)
                            assert result == {"affected": 2}, (predicate, direction, pending_id, result)
                            assert waited == should_wait, (predicate, direction, pending_id, verb, waited)
                            with owner.cursor() as cursor:
                                cursor.execute("SELECT id,n FROM target_tie ORDER BY id")
                                actual = cursor.fetchall()
                            expected = tuple(
                                (key, int(key in selected))
                                for key in sorted((1, 2, 3, pending_id))
                                if verb == "UPDATE" or key not in selected
                            )
                            assert actual == expected, (predicate, direction, pending_id, verb, actual, expected)
                            print(
                                f"MySQL {version}: {verb} secondary tie={predicate}/{direction} pending={pending_id},{pending_rank} wait={waited}",
                                flush=True,
                            )
                        finally:
                            waiter.close()
                text_cases = (
                    ("utf8mb4_bin", "z", "id<='f'", False, ("b", "d")),
                    ("utf8mb4_bin", "a", "id<='f'", True, ("a", "b")),
                    ("utf8mb4_0900_ai_ci", "Ä", "id<='f'", True, ("Ä", "b")),
                    ("utf8mb4_bin", "Ä", "id IN ('a','b','d')", False, ("b", "d")),
                    ("utf8mb4_bin", "a", "id IN ('a','b','d')", True, ("a", "b")),
                    ("utf8mb4_0900_ai_ci", "Ä", "id IN ('a','b','d')", True, ("Ä", "b")),
                    ("utf8mb4_0900_ai_ci", "Ä", "id IN ('b','d','z')", False, ("b", "d")),
                )
                for collation, inserted_id, predicate, should_wait, selected in text_cases:
                    for verb in ("UPDATE", "DELETE"):
                        waiter = connect(socket)
                        try:
                            with owner.cursor() as cursor:
                                cursor.execute("DROP TABLE IF EXISTS target_text")
                                cursor.execute(
                                    "CREATE TABLE target_text(id VARCHAR(16) COLLATE " + collation
                                    + " PRIMARY KEY,rank_value INT,n INT,KEY ix_rank(rank_value))"
                                )
                                cursor.executemany(
                                    "INSERT INTO target_text VALUES(%s,%s,0)",
                                    (("b", 1), ("d", 2), ("f", 3)),
                                )
                                cursor.execute("BEGIN")
                                cursor.execute("INSERT INTO target_text VALUES(%s,0,0)", (inserted_id,))
                            with waiter.cursor() as cursor:
                                cursor.execute("USE probe")
                                cursor.execute("BEGIN")
                            started = threading.Event()
                            result = {}

                            def write_text():
                                try:
                                    with waiter.cursor() as cursor:
                                        started.set()
                                        command = "UPDATE target_text SET n=n+1" if verb == "UPDATE" else "DELETE FROM target_text"
                                        cursor.execute(
                                            command + f" WHERE {predicate} ORDER BY rank_value ASC LIMIT 2"
                                        )
                                        result["affected"] = cursor.rowcount
                                        cursor.execute("COMMIT")
                                except Exception as error:
                                    result["error"] = str(error)

                            thread = threading.Thread(target=write_text, daemon=True)
                            thread.start()
                            assert started.wait(2)
                            thread.join(0.25)
                            waited = thread.is_alive()
                            owner.commit()
                            thread.join(5)
                            assert not thread.is_alive(), (collation, inserted_id, result)
                            assert result == {"affected": 2}, (collation, inserted_id, result)
                            assert waited == should_wait, (collation, inserted_id, verb, waited)
                            with owner.cursor() as cursor:
                                cursor.execute("SELECT id,n FROM target_text")
                                actual = dict(cursor.fetchall())
                            expected = {key: int(key in selected) for key in ("b", "d", "f", inserted_id)}
                            if verb == "DELETE":
                                expected = {key: value for key, value in expected.items() if key not in selected}
                            assert actual == expected, (collation, inserted_id, verb, actual, expected)
                            print(
                                f"MySQL {version}: {verb} text primary={collation} pending={inserted_id} predicate={predicate} wait={waited}",
                                flush=True,
                            )
                        finally:
                            waiter.close()
            finally:
                if owner:
                    owner.close()
                if server and server.poll() is None:
                    subprocess.run(
                        ["mysqladmin", "--no-defaults", f"--socket={socket}", "-uroot", "shutdown"],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    )
                    server.wait(timeout=20)


if __name__ == "__main__":
    main()
