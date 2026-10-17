"""Check ordered LIMIT write rebasing against a disposable MySQL 8.4 server."""

import itertools
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
    with tempfile.TemporaryDirectory(prefix="fsdb-limited-write-") as directory:
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

                cases = 0
                for limit in (1, 2):
                    for indexed in (False, True):
                        for scenario in ("predicate", "sort_key"):
                            for verb in ("UPDATE", "DELETE"):
                                waiter = connect(socket)
                                try:
                                    with owner.cursor() as cursor:
                                        cursor.execute("DROP TABLE IF EXISTS target")
                                        index = ", KEY ix_n(n)" if indexed else ""
                                        cursor.execute(f"CREATE TABLE target(id INT PRIMARY KEY,n INT{index})")
                                        second_value = 0 if scenario == "predicate" else 1
                                        rows = f"(1,0),(2,{second_value})"
                                        if limit == 2:
                                            third_value = 0 if scenario == "predicate" else 2
                                            rows += f",(3,{third_value})"
                                        cursor.execute(f"INSERT INTO target VALUES{rows}")
                                        cursor.execute("BEGIN")
                                        owner_value = -1 if scenario == "predicate" else limit + 1
                                        cursor.execute(f"UPDATE target SET n={owner_value} WHERE id=1")
                                    with waiter.cursor() as cursor:
                                        cursor.execute("USE probe")
                                        cursor.execute("BEGIN")

                                    ordering = "id" if scenario == "predicate" else "n,id"
                                    action = (
                                        "UPDATE target SET n=n+1" if scenario == "predicate" else
                                        "UPDATE target SET n=n+10"
                                    ) if verb == "UPDATE" else "DELETE FROM target"
                                    statement = f"{action} WHERE n>=0 ORDER BY {ordering} LIMIT {limit}"
                                    started = threading.Event()
                                    outcome = {}

                                    def waiting_write():
                                        try:
                                            with waiter.cursor() as cursor:
                                                started.set()
                                                cursor.execute(statement)
                                                outcome["affected"] = cursor.rowcount
                                                cursor.execute("COMMIT")
                                        except Exception as error:
                                            outcome["error"] = error

                                    thread = threading.Thread(target=waiting_write, daemon=True)
                                    thread.start()
                                    assert started.wait(2), statement
                                    thread.join(0.2)
                                    assert thread.is_alive(), (statement, "writer did not wait")
                                    with owner.cursor() as cursor:
                                        cursor.execute("COMMIT")
                                    thread.join(5)
                                    assert not thread.is_alive(), (statement, "writer did not resume")
                                    assert "error" not in outcome, outcome
                                    assert outcome["affected"] == limit, (statement, outcome)
                                    with owner.cursor() as cursor:
                                        cursor.execute("SELECT id,n FROM target ORDER BY id")
                                        actual = cursor.fetchall()
                                    if scenario == "predicate":
                                        expected = ((1, -1),)
                                        if verb == "UPDATE":
                                            expected += tuple((id, 1) for id in range(2, limit + 2))
                                    else:
                                        expected = ((1, owner_value),)
                                        if verb == "UPDATE":
                                            expected += tuple((id, id + 9) for id in range(2, limit + 2))
                                    assert actual == expected, (statement, indexed, actual)
                                    cases += 1
                                finally:
                                    waiter.close()

                for limit in (1, 2):
                    for indexed in (False, True):
                        for verb in ("UPDATE", "DELETE"):
                            waiter = connect(socket)
                            try:
                                with owner.cursor() as cursor:
                                    cursor.execute("DROP TABLE IF EXISTS target")
                                    index = ", KEY ix_n(n)" if indexed else ""
                                    cursor.execute(f"CREATE TABLE target(id INT PRIMARY KEY,n INT{index})")
                                    rows = ",".join(f"({id},0)" for id in range(1, limit + 2))
                                    cursor.execute(f"INSERT INTO target VALUES{rows}")
                                    cursor.execute("BEGIN")
                                    cursor.execute("DELETE FROM target WHERE id=1")
                                with waiter.cursor() as cursor:
                                    cursor.execute("USE probe")
                                    cursor.execute("BEGIN")
                                action = "UPDATE target SET n=n+1" if verb == "UPDATE" else "DELETE FROM target"
                                statement = f"{action} WHERE n>=0 ORDER BY id LIMIT {limit}"
                                started = threading.Event()
                                outcome = {}

                                def waiting_after_delete():
                                    try:
                                        with waiter.cursor() as cursor:
                                            started.set()
                                            cursor.execute(statement)
                                            outcome["affected"] = cursor.rowcount
                                            cursor.execute("COMMIT")
                                    except Exception as error:
                                        outcome["error"] = error

                                thread = threading.Thread(target=waiting_after_delete, daemon=True)
                                thread.start()
                                assert started.wait(2), statement
                                thread.join(0.2)
                                assert thread.is_alive(), (statement, "writer did not wait on deleted candidate")
                                with owner.cursor() as cursor:
                                    cursor.execute("COMMIT")
                                thread.join(5)
                                assert not thread.is_alive(), (statement, "writer did not resume")
                                assert outcome == {"affected": limit}, (statement, indexed, outcome)
                                with owner.cursor() as cursor:
                                    cursor.execute("SELECT id,n FROM target ORDER BY id")
                                    actual = cursor.fetchall()
                                expected = tuple((id, 1) for id in range(2, limit + 2)) if verb == "UPDATE" else ()
                                assert actual == expected, (statement, indexed, actual)
                                cases += 1
                            finally:
                                waiter.close()

                insert_cases = (
                    ("ASC", 0, True, (0,), 0, (1, 2), 1),
                    ("ASC", 100, False, (1,), 0, (1, 2), 1),
                    ("DESC", 100, True, (100,), 0, (1, 2), 1),
                    ("DESC", 0, False, (2,), 0, (1, 2), 1),
                    ("ASC", 100, True, (100,), -1, (1, 2), 1),
                    ("ASC", 0, True, (0, 1), 0, (1, 2, 3), 2),
                    ("ASC", 100, False, (1, 2), 0, (1, 2, 3), 2),
                    ("DESC", 100, True, (100, 3), 0, (1, 2, 3), 2),
                    ("DESC", 0, False, (3, 2), 0, (1, 2, 3), 2),
                )
                for id_type, verb, (direction, inserted_id, should_wait, changed_ids, initial_n, base_ids, limit) in itertools.product(
                    ("INT", "BIGINT UNSIGNED"),
                    ("UPDATE", "DELETE"),
                    insert_cases,
                ):
                    waiter = connect(socket)
                    try:
                        with owner.cursor() as cursor:
                            cursor.execute("DROP TABLE IF EXISTS target")
                            cursor.execute(f"CREATE TABLE target(id {id_type} PRIMARY KEY,n INT)")
                            rows = ",".join(f"({id},{initial_n})" for id in base_ids)
                            cursor.execute(f"INSERT INTO target VALUES{rows}")
                            cursor.execute("BEGIN")
                            cursor.execute(f"INSERT INTO target VALUES({inserted_id},0)")
                        with waiter.cursor() as cursor:
                            cursor.execute("USE probe")
                            cursor.execute("BEGIN")
                        started = threading.Event()
                        outcome = {}

                        def waiting_after_insert():
                            try:
                                with waiter.cursor() as cursor:
                                    started.set()
                                    action = "UPDATE target SET n=n+1" if verb == "UPDATE" else "DELETE FROM target"
                                    cursor.execute(f"{action} WHERE n>=0 ORDER BY id {direction} LIMIT {limit}")
                                    outcome["affected"] = cursor.rowcount
                                    cursor.execute("COMMIT")
                            except Exception as error:
                                outcome["error"] = error

                        thread = threading.Thread(target=waiting_after_insert, daemon=True)
                        thread.start()
                        assert started.wait(2)
                        thread.join(0.2)
                        waited = thread.is_alive()
                        with owner.cursor() as cursor:
                            cursor.execute("COMMIT")
                        thread.join(5)
                        assert not thread.is_alive(), "writer did not complete after insert commit"
                        assert outcome == {"affected": limit}, outcome
                        with owner.cursor() as cursor:
                            cursor.execute("SELECT id,n FROM target ORDER BY id")
                            inserted_result = cursor.fetchall()
                        assert waited == should_wait, (id_type, verb, direction, inserted_id, waited)
                        expected = tuple(
                            (id, 1 if id in changed_ids else 0 if id == inserted_id else initial_n)
                            for id in sorted((*base_ids, inserted_id))
                            if verb == "UPDATE" or id not in changed_ids
                        )
                        assert inserted_result == expected, (id_type, verb, direction, inserted_id, inserted_result)
                    finally:
                        waiter.close()

                date_cases = (
                    ("ASC", "2026-01-01", True, "2026-01-01"),
                    ("ASC", "2026-01-04", False, "2026-01-02"),
                    ("DESC", "2026-01-04", True, "2026-01-04"),
                    ("DESC", "2026-01-01", False, "2026-01-03"),
                )
                for verb, (direction, inserted_id, should_wait, changed_id) in itertools.product(
                    ("UPDATE", "DELETE"), date_cases
                ):
                    waiter = connect(socket)
                    try:
                        with owner.cursor() as cursor:
                            cursor.execute("DROP TABLE IF EXISTS target")
                            cursor.execute("CREATE TABLE target(id DATE PRIMARY KEY,n INT)")
                            cursor.execute("INSERT INTO target VALUES('2026-01-02',0),('2026-01-03',0)")
                            cursor.execute("BEGIN")
                            cursor.execute(f"INSERT INTO target VALUES('{inserted_id}',0)")
                        with waiter.cursor() as cursor:
                            cursor.execute("USE probe")
                            cursor.execute("BEGIN")
                        action = "UPDATE target SET n=n+1" if verb == "UPDATE" else "DELETE FROM target"
                        statement = f"{action} WHERE n>=0 ORDER BY id {direction} LIMIT 1"
                        started = threading.Event()
                        outcome = {}

                        def waiting_after_date_insert():
                            try:
                                with waiter.cursor() as cursor:
                                    started.set()
                                    cursor.execute(statement)
                                    outcome["affected"] = cursor.rowcount
                                    cursor.execute("COMMIT")
                            except Exception as error:
                                outcome["error"] = error

                        thread = threading.Thread(target=waiting_after_date_insert, daemon=True)
                        thread.start()
                        assert started.wait(2), statement
                        thread.join(0.2)
                        waited = thread.is_alive()
                        with owner.cursor() as cursor:
                            cursor.execute("COMMIT")
                        thread.join(5)
                        assert not thread.is_alive(), statement
                        assert outcome == {"affected": 1}, outcome
                        with owner.cursor() as cursor:
                            cursor.execute("SELECT CAST(id AS CHAR),n FROM target ORDER BY id")
                            actual = cursor.fetchall()
                        assert waited == should_wait, (statement, inserted_id, waited)
                        expected = tuple(
                            (id, 1 if id == changed_id else 0)
                            for id in sorted(("2026-01-02", "2026-01-03", inserted_id))
                            if verb == "UPDATE" or id != changed_id
                        )
                        assert actual == expected, (statement, inserted_id, actual, expected)
                    finally:
                        waiter.close()

                composite_cases = (
                    ("ASC", "ASC", (1, 0), True, ((1, 0),), 1),
                    ("ASC", "ASC", (3, 1), False, ((1, 1),), 1),
                    ("DESC", "DESC", (3, 1), True, ((3, 1),), 1),
                    ("DESC", "DESC", (1, 0), False, ((2, 1),), 1),
                    ("ASC", "DESC", (1, 4), True, ((1, 4),), 1),
                    ("ASC", "DESC", (2, 0), True, ((1, 3),), 1),
                    ("DESC", "ASC", (2, 0), True, ((2, 0),), 1),
                    ("DESC", "ASC", (1, 4), True, ((2, 1),), 1),
                    ("ASC", "ASC", (1, 2), True, ((1, 1), (1, 2)), 2),
                    ("ASC", "ASC", (3, 1), False, ((1, 1), (1, 3)), 2),
                    ("DESC", "DESC", (2, 2), True, ((2, 2), (2, 1)), 2),
                    ("DESC", "DESC", (1, 0), False, ((2, 1), (1, 3)), 2),
                )
                base_keys = ((1, 1), (1, 3), (2, 1))
                for verb, (first_direction, second_direction, inserted_key, should_wait, changed_keys, limit) in itertools.product(
                    ("UPDATE", "DELETE"), composite_cases
                ):
                    waiter = connect(socket)
                    try:
                        with owner.cursor() as cursor:
                            cursor.execute("DROP TABLE IF EXISTS target")
                            cursor.execute("CREATE TABLE target(a INT,b INT,n INT,PRIMARY KEY(a,b))")
                            rows = ",".join(f"({a},{b},0)" for a,b in base_keys)
                            cursor.execute(f"INSERT INTO target VALUES{rows}")
                            cursor.execute("BEGIN")
                            cursor.execute(f"INSERT INTO target VALUES({inserted_key[0]},{inserted_key[1]},0)")
                        with waiter.cursor() as cursor:
                            cursor.execute("USE probe")
                            cursor.execute("BEGIN")
                        action = "UPDATE target SET n=n+1" if verb == "UPDATE" else "DELETE FROM target"
                        statement = f"{action} WHERE n>=0 ORDER BY a {first_direction},b {second_direction} LIMIT {limit}"
                        started = threading.Event()
                        outcome = {}

                        def waiting_after_composite_insert():
                            try:
                                with waiter.cursor() as cursor:
                                    started.set()
                                    cursor.execute(statement)
                                    outcome["affected"] = cursor.rowcount
                                    cursor.execute("COMMIT")
                            except Exception as error:
                                outcome["error"] = error

                        thread = threading.Thread(target=waiting_after_composite_insert, daemon=True)
                        thread.start()
                        assert started.wait(2), statement
                        thread.join(0.2)
                        waited = thread.is_alive()
                        with owner.cursor() as cursor:
                            cursor.execute("COMMIT")
                        thread.join(5)
                        assert not thread.is_alive(), statement
                        assert outcome == {"affected": limit}, outcome
                        with owner.cursor() as cursor:
                            cursor.execute("SELECT a,b,n FROM target ORDER BY a,b")
                            actual = cursor.fetchall()
                        assert waited == should_wait, (statement, inserted_key, waited)
                        expected = tuple(
                            (a,b,1 if (a,b) in changed_keys else 0)
                            for a,b in sorted((*base_keys,inserted_key))
                            if verb == "UPDATE" or (a,b) not in changed_keys
                        )
                        assert actual == expected, (statement, inserted_key, actual, expected)
                    finally:
                        waiter.close()

                print(f"MySQL {version}: {cases} ordered LIMIT 1/2 writes reselected after updates/deletes; "
                      f"{len(insert_cases) * 4} integer, {len(date_cases) * 2} DATE, and "
                      f"{len(composite_cases) * 2} composite pending-insert cases covered ordered frontiers")
            finally:
                if owner is not None:
                    owner.close()
                if server is not None and server.poll() is None:
                    subprocess.run(
                        ["mysqladmin", "--no-defaults", f"--socket={socket}", "-uroot", "shutdown"],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    )
                    server.wait(timeout=20)


if __name__ == "__main__":
    main()
