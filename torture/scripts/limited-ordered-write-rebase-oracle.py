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
                for limit in (1, 2, 3):
                    for indexed in (False, True):
                        for scenario in ("predicate", "sort_key"):
                            for verb in ("UPDATE", "DELETE"):
                                waiter = connect(socket)
                                try:
                                    with owner.cursor() as cursor:
                                        cursor.execute("DROP TABLE IF EXISTS target")
                                        index = ", KEY ix_n(n)" if indexed else ""
                                        cursor.execute(f"CREATE TABLE target(id INT PRIMARY KEY,n INT{index})")
                                        rows = ",".join(
                                            f"({id},{0 if scenario == 'predicate' else id - 1})"
                                            for id in range(1, limit + 2)
                                        )
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

                for limit in (1, 2, 3):
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

                waiter = connect(socket)
                try:
                    with owner.cursor() as cursor:
                        cursor.execute("DROP TABLE IF EXISTS target")
                        cursor.execute("CREATE TABLE target(id INT PRIMARY KEY,rank_value INT,KEY ix_rank(rank_value))")
                        cursor.execute("BEGIN")
                        cursor.execute("INSERT INTO target VALUES(1,0)")
                    with waiter.cursor() as cursor:
                        cursor.execute("USE probe")
                        cursor.execute("BEGIN")
                    started = threading.Event()
                    outcome = {}

                    def equal_secondary_insert():
                        try:
                            with waiter.cursor() as cursor:
                                started.set()
                                cursor.execute("INSERT INTO target VALUES(2,0)")
                                outcome["affected"] = cursor.rowcount
                                cursor.execute("COMMIT")
                        except Exception as error:
                            outcome["error"] = error

                    thread = threading.Thread(target=equal_secondary_insert, daemon=True)
                    thread.start()
                    assert started.wait(2), "equal secondary insert starts"
                    thread.join(0.2)
                    waited = thread.is_alive()
                    owner.commit()
                    thread.join(5)
                    assert not waited, "non-unique secondary keys do not serialize distinct inserts"
                    assert outcome == {"affected": 1}, outcome
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

                text_cases = (
                    ("ASC", "", True, ("",), 1, None),
                    ("ASC", "a", True, ("a",), 1, None),
                    ("ASC", "z", False, ("b",), 1, None),
                    ("DESC", "z", True, ("z",), 1, None),
                    ("DESC", "a", False, ("d",), 1, None),
                    ("ASC", "c", True, ("b", "c"), 2, None),
                    ("ASC", "z", False, ("b", "d"), 2, None),
                    ("DESC", "c", True, ("d", "c"), 2, None),
                    ("DESC", "a", False, ("d", "b"), 2, None),
                    ("ASC", "Ä", False, ("b", "d"), 2, "utf8mb4_bin"),
                    ("DESC", "Ä", True, ("Ä", "d"), 2, "utf8mb4_bin"),
                    ("ASC", "Ä", True, ("Ä", "b"), 2, "utf8mb4_0900_ai_ci"),
                    ("DESC", "Ä", False, ("d", "b"), 2, "utf8mb4_0900_ai_ci"),
                )
                text_case_count = 0
                for collation, verb, (direction, inserted_id, should_wait, changed_ids, limit, case_collation) in itertools.product(
                    ("utf8mb4_bin", "utf8mb4_0900_ai_ci"), ("UPDATE", "DELETE"), text_cases
                ):
                    if case_collation is not None and case_collation != collation:
                        continue
                    text_case_count += 1
                    waiter = connect(socket)
                    try:
                        with owner.cursor() as cursor:
                            cursor.execute("DROP TABLE IF EXISTS target")
                            cursor.execute(
                                f"CREATE TABLE target(id VARCHAR(10) COLLATE {collation} PRIMARY KEY,n INT)"
                            )
                            cursor.execute("INSERT INTO target VALUES('b',0),('d',0)")
                            cursor.execute("BEGIN")
                            cursor.execute(f"INSERT INTO target VALUES('{inserted_id}',0)")
                        with waiter.cursor() as cursor:
                            cursor.execute("USE probe")
                            cursor.execute("BEGIN")
                        action = "UPDATE target SET n=n+1" if verb == "UPDATE" else "DELETE FROM target"
                        statement = f"{action} WHERE n>=0 ORDER BY id {direction} LIMIT {limit}"
                        started = threading.Event()
                        outcome = {}

                        def waiting_after_text_insert():
                            try:
                                with waiter.cursor() as cursor:
                                    started.set()
                                    cursor.execute(statement)
                                    outcome["affected"] = cursor.rowcount
                                    cursor.execute("COMMIT")
                            except Exception as error:
                                outcome["error"] = error

                        thread = threading.Thread(target=waiting_after_text_insert, daemon=True)
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
                            cursor.execute("SELECT id,n FROM target ORDER BY id")
                            actual = cursor.fetchall()
                        assert waited == should_wait, (statement, collation, inserted_id, waited)
                        expected_order = sorted(
                            ("b", "d", inserted_id),
                            key=lambda value: "a" if collation == "utf8mb4_0900_ai_ci" and value == "Ä" else value,
                        )
                        expected = tuple(
                            (id, 1 if id in changed_ids else 0)
                            for id in expected_order
                            if verb == "UPDATE" or id not in changed_ids
                        )
                        assert actual == expected, (statement, collation, inserted_id, actual, expected)
                    finally:
                        waiter.close()

                composite_text_cases = (
                    ("ASC", "ASC", ("b", 0), True, (("b", 0),), 1, None),
                    ("ASC", "ASC", ("b", 4), False, (("b", 1),), 1, None),
                    ("ASC", "ASC", ("b", 2), True, (("b", 1), ("b", 2)), 2, None),
                    ("ASC", "ASC", ("Ä", 0), False, (("b", 1),), 1, "utf8mb4_bin"),
                    ("ASC", "ASC", ("Ä", 0), True, (("Ä", 0),), 1, "utf8mb4_0900_ai_ci"),
                    ("DESC", "DESC", ("Ä", 0), True, (("Ä", 0),), 1, "utf8mb4_bin"),
                    ("DESC", "DESC", ("Ä", 0), False, (("d", 1),), 1, "utf8mb4_0900_ai_ci"),
                    ("ASC", "DESC", ("b", 2), True, (("b", 3),), 1, None),
                    ("DESC", "ASC", ("b", 2), True, (("d", 1),), 1, None),
                )
                composite_text_count = 0
                base_text_keys = (("b", 1), ("b", 3), ("d", 1))
                for collation, verb, case in itertools.product(
                    ("utf8mb4_bin", "utf8mb4_0900_ai_ci"), ("UPDATE", "DELETE"), composite_text_cases
                ):
                    first_direction, second_direction, inserted_key, should_wait, changed_keys, limit, case_collation = case
                    if case_collation is not None and case_collation != collation:
                        continue
                    composite_text_count += 1
                    waiter = connect(socket)
                    try:
                        with owner.cursor() as cursor:
                            cursor.execute("DROP TABLE IF EXISTS target")
                            cursor.execute(
                                f"CREATE TABLE target(a VARCHAR(10) COLLATE {collation},b INT,n INT,PRIMARY KEY(a,b))"
                            )
                            rows = ",".join(f"('{a}',{b},0)" for a,b in base_text_keys)
                            cursor.execute(f"INSERT INTO target VALUES{rows}")
                            cursor.execute("BEGIN")
                            cursor.execute(f"INSERT INTO target VALUES('{inserted_key[0]}',{inserted_key[1]},0)")
                        with waiter.cursor() as cursor:
                            cursor.execute("USE probe")
                            cursor.execute("BEGIN")
                        action = "UPDATE target SET n=n+1" if verb == "UPDATE" else "DELETE FROM target"
                        statement = f"{action} WHERE n>=0 ORDER BY a {first_direction},b {second_direction} LIMIT {limit}"
                        started = threading.Event()
                        outcome = {}

                        def waiting_after_composite_text_insert():
                            try:
                                with waiter.cursor() as cursor:
                                    started.set()
                                    cursor.execute(statement)
                                    outcome["affected"] = cursor.rowcount
                                    cursor.execute("COMMIT")
                            except Exception as error:
                                outcome["error"] = error

                        thread = threading.Thread(target=waiting_after_composite_text_insert, daemon=True)
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
                        assert waited == should_wait, (statement, collation, inserted_key, waited)
                        actual_keys = tuple((a,b) for a,b,_ in actual)
                        assert set(actual_keys) == (
                            set((*base_text_keys, inserted_key)) if verb == "UPDATE"
                            else set((*base_text_keys, inserted_key)) - set(changed_keys)
                        ), (statement, actual)
                        assert all(n == (1 if (a,b) in changed_keys else 0) for a,b,n in actual), (statement, actual)
                    finally:
                        waiter.close()

                def check_secondary_insert(verb, limit, direction, inserted_id, inserted_rank, should_wait, changed_ids):
                    waiter = connect(socket)
                    try:
                        with owner.cursor() as cursor:
                            cursor.execute("DROP TABLE IF EXISTS target")
                            cursor.execute("CREATE TABLE target(id INT PRIMARY KEY,rank_value INT,n INT,KEY ix_rank(rank_value))")
                            cursor.executemany(
                                "INSERT INTO target VALUES(%s,%s,0)",
                                [(row_id, row_id) for row_id in range(1, limit + 2)],
                            )
                            cursor.execute("BEGIN")
                            cursor.execute("INSERT INTO target VALUES(%s,%s,0)", (inserted_id, inserted_rank))
                        with waiter.cursor() as cursor:
                            cursor.execute("USE probe")
                            cursor.execute("BEGIN")
                        started = threading.Event()
                        outcome = {}

                        def waiting_after_secondary_insert():
                            try:
                                with waiter.cursor() as cursor:
                                    started.set()
                                    action = "UPDATE target SET n=n+1" if verb == "UPDATE" else "DELETE FROM target"
                                    cursor.execute(f"{action} WHERE n>=0 ORDER BY rank_value {direction} LIMIT {limit}")
                                    outcome["affected"] = cursor.rowcount
                                    cursor.execute("COMMIT")
                            except Exception as error:
                                outcome["error"] = error

                        thread = threading.Thread(target=waiting_after_secondary_insert, daemon=True)
                        thread.start()
                        assert started.wait(2), "secondary ordered writer starts"
                        thread.join(0.2)
                        waited = thread.is_alive()
                        owner.commit()
                        thread.join(5)
                        assert not thread.is_alive(), "secondary ordered writer resumes"
                        with owner.cursor() as cursor:
                            cursor.execute("SELECT id,rank_value,n FROM target ORDER BY id")
                            final_rows = cursor.fetchall()
                        ranks = {row_id: row_id for row_id in range(1, limit + 2)}
                        ranks[inserted_id] = inserted_rank
                        expected_rows = tuple(
                            (row_id, ranks[row_id], int(row_id in changed_ids))
                            for row_id in sorted(ranks)
                            if verb == "UPDATE" or row_id not in changed_ids
                        )
                        assert waited == should_wait, (verb, limit, direction, inserted_id, waited)
                        assert outcome == {"affected": limit}, outcome
                        assert final_rows == expected_rows, (verb, limit, direction, inserted_id, final_rows)
                    finally:
                        waiter.close()

                secondary_cases = (
                    (1, "ASC", 0, 0, True, (0,)),
                    (1, "ASC", 3, 3, False, (1,)),
                    (1, "DESC", 3, 3, True, (3,)),
                    (1, "DESC", 0, 0, False, (2,)),
                    (1, "ASC", 0, None, True, (0,)),
                    (2, "ASC", 0, 0, True, (0, 1)),
                    (2, "ASC", 4, 4, True, (1, 2)),
                    (2, "DESC", 4, 4, True, (4, 3)),
                    (2, "DESC", 0, 0, True, (3, 2)),
                    (2, "ASC", 0, None, True, (0, 1)),
                )
                for verb, case in itertools.product(("UPDATE", "DELETE"), secondary_cases):
                    check_secondary_insert(verb, *case)

                waiter = connect(socket)
                try:
                    with owner.cursor() as cursor:
                        cursor.execute("DROP TABLE IF EXISTS target")
                        cursor.execute("CREATE TABLE target(id INT PRIMARY KEY,rank_value INT,n INT,KEY ix_rank(rank_value))")
                        cursor.executemany("INSERT INTO target VALUES(%s,%s,0)", [(i, i) for i in range(1, 1001)])
                        cursor.execute("BEGIN")
                        cursor.execute("INSERT INTO target VALUES(2000,2000,0)")
                    with waiter.cursor() as cursor:
                        cursor.execute("USE probe")
                        cursor.execute("BEGIN")
                    started = threading.Event()
                    outcome = {}

                    def waiting_after_large_insert():
                        try:
                            with waiter.cursor() as cursor:
                                started.set()
                                cursor.execute("UPDATE target SET n=n+1 WHERE n>=0 ORDER BY rank_value ASC LIMIT 2")
                                outcome["affected"] = cursor.rowcount
                                cursor.execute("COMMIT")
                        except Exception as error:
                            outcome["error"] = error

                    thread = threading.Thread(target=waiting_after_large_insert, daemon=True)
                    thread.start()
                    assert started.wait(2)
                    thread.join(0.2)
                    assert not thread.is_alive(), "large secondary-order writer need not wait beyond the frontier"
                    owner.commit()
                    thread.join(5)
                    assert outcome == {"affected": 2}, outcome
                    with owner.cursor() as cursor:
                        cursor.execute("SELECT id,n FROM target WHERE n=1 OR id=2000 ORDER BY id")
                        assert cursor.fetchall() == ((1, 1), (2, 1), (2000, 0))
                finally:
                    waiter.close()

                print(f"MySQL {version}: {cases} ordered LIMIT 1/2/3 writes reselected after updates/deletes; "
                      f"{len(insert_cases) * 4} integer, {len(date_cases) * 2} DATE, "
                      f"{len(composite_cases) * 2} composite, {text_case_count} text, "
                      f"{composite_text_count} composite text, and {len(secondary_cases) * 2} secondary-order "
                      "pending-insert cases covered ordered frontiers; equal secondary inserts stayed independent")
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
