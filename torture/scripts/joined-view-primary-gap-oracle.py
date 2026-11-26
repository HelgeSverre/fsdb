"""Check primary-key gap schedules on disposable native MySQL and Fsdb servers.

Set MYSQL_PORT to a MySQL 8.4.11 loopback port. Set FSDB_PORT to compare Fsdb.
The script connects as root without a password and creates a disposable schema.
"""

import os
from contextlib import ExitStack, contextmanager
from time import monotonic, sleep
from uuid import uuid4

import pymysql

SETUP = (
    "CREATE TABLE parent(id INT PRIMARY KEY,n INT)",
    "CREATE TABLE partner(id INT,seq INT,delta INT,PRIMARY KEY(id,seq))",
    "CREATE TABLE gate(id INT PRIMARY KEY,delta INT)",
    "INSERT INTO parent VALUES (3,10),(4,10)",
    "INSERT INTO partner VALUES (3,20,3),(4,20,3)",
    "CREATE VIEW active AS SELECT p.id,p.n,s.seq,s.delta FROM parent p JOIN partner s ON p.id=s.id",
)

TIMEOUT = 1205
DUPLICATE = 1062


def update_at(key):
    return f"""
        UPDATE active a JOIN gate g ON g.id=a.id
        SET a.n=a.n+a.delta+g.delta WHERE a.id={key} AND a.seq BETWEEN 20 AND 22
    """


def connect(port):
    return pymysql.connect(host="127.0.0.1", port=port, user="root", autocommit=True)


def execute(connection, sql):
    with connection.cursor() as cursor:
        return cursor.execute(sql)


def scalar(connection, sql):
    with connection.cursor() as cursor:
        cursor.execute(sql)
        return cursor.fetchone()[0]


def outcome(connection, sql):
    try:
        return execute(connection, sql)
    except (pymysql.err.OperationalError, pymysql.err.IntegrityError) as error:
        return error.args[0]


@contextmanager
def fixture(port, schema, mysql):
    with connect(port) as admin:
        version = scalar(admin, "SELECT VERSION()")
        if mysql:
            assert version.startswith("8.4.11"), version
        else:
            assert version.endswith("-fsdb"), version
        execute(admin, f"CREATE DATABASE {schema}")
        try:
            execute(admin, f"USE {schema}")
            for statement in SETUP:
                execute(admin, statement)
            with ExitStack() as stack:
                writer, owner, probe = (
                    stack.enter_context(connect(port)) for _ in range(3)
                )
                for connection in (writer, owner, probe):
                    execute(connection, f"USE {schema}")
                    execute(connection, "SET SESSION innodb_lock_wait_timeout=1")
                yield version, admin, writer, owner, probe
        finally:
            execute(admin, f"DROP DATABASE {schema}")


def check(port, mysql):
    engine = "MySQL" if mysql else "Fsdb"
    schema = "fsdb_primary_gap_" + uuid4().hex
    failures = checks = 0
    update = update_at(3)

    def expect(label, actual, expected):
        nonlocal failures, checks
        checks += 1
        if actual != expected:
            print(f"{engine} {label}: got {actual!r}, expected {expected!r}")
            failures += 1

    with fixture(port, schema, mysql) as (version, admin, writer, owner, probe):

        def reset_gate(rows="(-5,1),(1,7),(5,13),(9,19)"):
            execute(admin, "TRUNCATE TABLE gate")
            if rows:
                execute(admin, "INSERT INTO gate VALUES " + rows)

        def isolation(connection, level):
            execute(connection, "SET SESSION TRANSACTION ISOLATION LEVEL " + level)

        def attempt(sql):
            execute(probe, "START TRANSACTION")
            try:
                return outcome(probe, sql)
            finally:
                execute(probe, "ROLLBACK")

        def insert(keys):
            return tuple(attempt(f"INSERT INTO gate VALUES ({key},0)") for key in keys)

        def insert_after_purge(key):
            deadline = monotonic() + 4
            result = insert((key,))
            while result == (1,) and monotonic() < deadline:
                sleep(0.05)
                result = insert((key,))
            return result

        reset_gate()
        for level, expected in (
            ("READ UNCOMMITTED", (1, 1, 1, 1, 1)),
            ("READ COMMITTED", (1, 1, 1, 1, 1)),
            ("REPEATABLE READ", (1, TIMEOUT, TIMEOUT, TIMEOUT, 1)),
            ("SERIALIZABLE", (1, TIMEOUT, TIMEOUT, TIMEOUT, 1)),
        ):
            isolation(writer, level)
            execute(writer, "START TRANSACTION")
            expect(f"{level} absent lookup", outcome(writer, update), 0)
            expect(f"{level} inserts at 0,2,3,4,6", insert((0, 2, 3, 4, 6)), expected)
            execute(writer, "ROLLBACK")

        isolation(writer, "REPEATABLE READ")
        isolation(owner, "REPEATABLE READ")
        execute(writer, "START TRANSACTION")
        execute(writer, "SAVEPOINT before_lookup")
        expect("lookup after savepoint", outcome(writer, update), 0)
        expect(
            "moving a key into the gap",
            attempt("UPDATE gate SET id=4 WHERE id=5"),
            TIMEOUT,
        )
        expect(
            "changing the upper record",
            attempt("UPDATE gate SET delta=55 WHERE id=5"),
            1,
        )
        execute(writer, "ROLLBACK TO SAVEPOINT before_lookup")
        expect("inserts after savepoint rollback", insert((2, 3, 4)), (1, 1, 1))
        execute(writer, "ROLLBACK")

        execute(writer, "START TRANSACTION")
        expect("first shared lookup", outcome(writer, update), 0)
        execute(owner, "START TRANSACTION")
        expect("second shared lookup", outcome(owner, update_at(4)), 0)
        execute(writer, "ROLLBACK")
        expect("insertion with one holder remaining", insert((3,)), (TIMEOUT,))
        execute(owner, "ROLLBACK")
        expect("insertion after both holders release", insert((3,)), (1,))

        for endpoint, expected in (
            (1, (1, 1, TIMEOUT, TIMEOUT, TIMEOUT, DUPLICATE, 1)),
            (5, (1, DUPLICATE, TIMEOUT, TIMEOUT, TIMEOUT, TIMEOUT, TIMEOUT)),
        ):
            reset_gate()
            execute(writer, "START TRANSACTION")
            expect(
                f"lookup before deleting endpoint {endpoint}",
                outcome(writer, update),
                0,
            )
            expect(
                f"delete endpoint {endpoint}",
                outcome(owner, f"DELETE FROM gate WHERE id={endpoint}"),
                1,
            )
            expect(
                f"inserts at 0–6 after deleting endpoint {endpoint}",
                insert(range(7)),
                expected,
            )
            execute(writer, "ROLLBACK")

        isolation(admin, "REPEATABLE READ")
        for view_owner in ("gap holder", "other session"):
            reset_gate()
            reader = writer if view_owner == "gap holder" else admin
            execute(reader, "START TRANSACTION")
            expect(
                f"read view opened by {view_owner}",
                scalar(reader, "SELECT COUNT(*) FROM gate"),
                4,
            )
            if reader is not writer:
                execute(writer, "START TRANSACTION")
            expect(f"lookup with {view_owner} read view", outcome(writer, update), 0)
            expect(
                f"delete successor with {view_owner} read view",
                outcome(owner, "DELETE FROM gate WHERE id=5"),
                1,
            )
            expect(
                f"inserts at 4,6 with {view_owner} read view",
                insert((4, 6)),
                (TIMEOUT, 1),
            )
            if reader is not writer:
                execute(reader, "ROLLBACK")
                expect(
                    "insert at 6 after the independent reader ends",
                    insert_after_purge(6),
                    (TIMEOUT,),
                )
            execute(writer, "ROLLBACK")

        for pending_predecessor in (False, True):
            reset_gate("(0,7),(10,13)")
            if pending_predecessor:
                execute(owner, "START TRANSACTION")
            execute(owner, "INSERT INTO gate VALUES (2,0)")
            execute(writer, "START TRANSACTION")
            label = (
                "pending predecessor"
                if pending_predecessor
                else "committed predecessor"
            )
            expect(f"lookup with {label} at 2", outcome(writer, update), 0)
            if pending_predecessor:
                execute(owner, "COMMIT")
            execute(admin, "START TRANSACTION")
            expect(
                f"read view includes {label}",
                scalar(admin, "SELECT COUNT(*) FROM gate"),
                3,
            )
            expect(f"delete {label}", outcome(owner, "DELETE FROM gate WHERE id=2"), 1)
            expect(f"insert while the old {label} is pinned", insert((1,)), (1,))
            execute(admin, "ROLLBACK")
            expect(f"insert after purge of {label}", insert_after_purge(1), (TIMEOUT,))
            execute(writer, "ROLLBACK")

        for label, rows, expected in (
            ("before the first key", "(5,13),(9,19)", (TIMEOUT, TIMEOUT, 1)),
            ("after the last key", "(-5,1),(1,7)", (1, TIMEOUT, TIMEOUT)),
            ("in an empty table", "", (TIMEOUT, TIMEOUT, TIMEOUT)),
        ):
            reset_gate(rows)
            execute(writer, "START TRANSACTION")
            expect(f"lookup {label}", outcome(writer, update), 0)
            expect(f"inserts at 0,3,6 {label}", insert((0, 3, 6)), expected)
            execute(writer, "ROLLBACK")

        for boundary, key, candidates, blocked in (
            ("predecessor", 2, (1, 3, 5), (1, TIMEOUT, TIMEOUT)),
            ("successor", 6, (2, 4, 7), (TIMEOUT, TIMEOUT, 1)),
        ):
            for ending in ("COMMIT", "ROLLBACK"):
                reset_gate("(0,7),(10,13)")
                execute(owner, "START TRANSACTION")
                execute(owner, f"INSERT INTO gate VALUES ({key},0)")
                execute(writer, "START TRANSACTION")
                expect(
                    f"lookup beside a pending {boundary} before {ending}",
                    outcome(writer, update),
                    0,
                )
                expect(
                    f"inserts at {candidates} while {boundary} is pending before {ending}",
                    insert(candidates),
                    blocked,
                )
                execute(owner, ending)
                expected = blocked if ending == "COMMIT" else (TIMEOUT,) * len(blocked)
                expect(
                    f"inserts at {candidates} after {boundary} {ending}",
                    insert(candidates),
                    expected,
                )
                execute(writer, "ROLLBACK")

        reset_gate()
        for changed, expected in (
            (False, (1, 1, 1)),
            (True, (TIMEOUT, TIMEOUT, TIMEOUT)),
        ):
            xid = schema + ("_write" if changed else "_read")
            execute(writer, f"XA START '{xid}'")
            if changed:
                expect(
                    "write before XA lookup",
                    outcome(writer, "UPDATE parent SET n=20 WHERE id=3"),
                    1,
                )
            expect(f"XA lookup with write={changed}", outcome(writer, update), 0)
            execute(writer, f"XA END '{xid}'")
            execute(writer, f"XA PREPARE '{xid}'")
            try:
                expect(
                    f"prepared XA inserts with write={changed}",
                    insert((2, 3, 4)),
                    expected,
                )
            finally:
                execute(writer, f"XA ROLLBACK '{xid}'")
            expect(
                f"inserts after XA rollback with write={changed}",
                insert((2, 3, 4)),
                (1, 1, 1),
            )
            expect(
                f"parent after XA rollback with write={changed}",
                scalar(admin, "SELECT n FROM parent WHERE id=3"),
                10,
            )
    print(f"{engine} {version}: {checks - failures}/{checks} schedule checks matched")
    return failures


if __name__ == "__main__":
    mysql_port = int(os.environ["MYSQL_PORT"])
    fsdb_port = os.environ.get("FSDB_PORT")
    failures = check(mysql_port, mysql=True)
    if fsdb_port is not None:
        failures += check(int(fsdb_port), mysql=False)
    raise SystemExit(bool(failures))
