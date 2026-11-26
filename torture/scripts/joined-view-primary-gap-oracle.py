"""Check primary-key gap schedules on disposable native MySQL and Fsdb servers.

Set MYSQL_PORT to a MySQL 8.4.11 loopback port. Set FSDB_PORT to compare Fsdb.
The script connects as root without a password and creates a disposable schema.
"""

import os
from concurrent.futures import ThreadPoolExecutor, TimeoutError
from contextlib import ExitStack, contextmanager
from threading import Event
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
    except (
        pymysql.err.OperationalError,
        pymysql.err.IntegrityError,
        pymysql.err.ProgrammingError,
    ) as error:
        return error.args[0]


def rolled_back_outcome(connection, sql):
    execute(connection, "START TRANSACTION")
    try:
        return outcome(connection, sql)
    finally:
        execute(connection, "ROLLBACK")


@contextmanager
def fixture(port, schema, mysql, setup=SETUP):
    with connect(port) as admin:
        version = scalar(admin, "SELECT VERSION()")
        if mysql:
            assert version.startswith("8.4.11"), version
        else:
            assert version.endswith("-fsdb"), version
        execute(admin, f"CREATE DATABASE {schema}")
        try:
            execute(admin, f"USE {schema}")
            for statement in setup:
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
            return rolled_back_outcome(probe, sql)

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

        isolation(admin, "REPEATABLE READ")
        for endpoint, expected in (
            (1, (1, 1, TIMEOUT, TIMEOUT, TIMEOUT, DUPLICATE, 1)),
            (5, (1, DUPLICATE, TIMEOUT, TIMEOUT, TIMEOUT, TIMEOUT, TIMEOUT)),
        ):
            reset_gate()
            pin_predecessor = endpoint == 1
            if pin_predecessor:
                execute(admin, "START TRANSACTION")
                expect(
                    "read view before deleting predecessor",
                    scalar(admin, "SELECT COUNT(*) FROM gate"),
                    4,
                )
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
            if pin_predecessor:
                execute(admin, "ROLLBACK")

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

        reference = schema + "_ref"
        execute(admin, f"CREATE DATABASE {reference}")
        try:
            execute(admin, f"CREATE TABLE {reference}.keys_for_gate(k INT PRIMARY KEY)")
            execute(admin, f"INSERT INTO {reference}.keys_for_gate VALUES (1),(5)")
            reset_gate("(1,7),(5,13)")
            execute(
                admin,
                "ALTER TABLE gate ADD CONSTRAINT fk_gate FOREIGN KEY(id) "
                f"REFERENCES {reference}.keys_for_gate(k) ON UPDATE CASCADE",
            )
            for label, sql in (
                ("direct qualified move", "UPDATE gate SET id=4 WHERE id=5"),
                (
                    "qualified cascade",
                    f"UPDATE {reference}.keys_for_gate SET k=4 WHERE k=5",
                ),
            ):
                direct = label == "direct qualified move"
                if direct:
                    execute(admin, f"INSERT INTO {reference}.keys_for_gate VALUES (4)")
                execute(writer, "START TRANSACTION")
                expect(f"lookup before {label}", outcome(writer, update), 0)
                expect(f"{label} into the gap", attempt(sql), TIMEOUT)
                execute(writer, "ROLLBACK")
                expect(f"{label} after gap release", attempt(sql), 1)
                if direct:
                    execute(admin, f"DELETE FROM {reference}.keys_for_gate WHERE k=4")
        finally:
            execute(admin, "DROP TABLE gate")
            execute(admin, f"DROP DATABASE {reference}")

    base = (1 << 64) - 10
    lower, lookup, upper = (base + offset for offset in (1, 5, 9))
    unsigned_setup = (
        "CREATE TABLE parent(id BIGINT UNSIGNED PRIMARY KEY,n INT)",
        "CREATE TABLE partner(id BIGINT UNSIGNED,seq INT,delta INT,PRIMARY KEY(id,seq))",
        "CREATE TABLE gate(id BIGINT UNSIGNED PRIMARY KEY,delta INT)",
        f"INSERT INTO parent VALUES ({lookup},10)",
        f"INSERT INTO partner VALUES ({lookup},20,3)",
        "CREATE VIEW active AS SELECT p.id,p.n,s.seq,s.delta FROM parent p JOIN partner s ON p.id=s.id",
    )
    with fixture(port, schema, mysql, unsigned_setup) as (
        _,
        admin,
        writer,
        owner,
        probe,
    ):
        execute(admin, f"INSERT INTO gate VALUES ({lower},7),({upper},13)")
        direct = update_at(lookup)
        projected = (
            "WITH gate_rows(gate_id,amount) AS (SELECT id,delta FROM gate) "
            "UPDATE active a JOIN gate_rows g ON g.gate_id=a.id "
            f"SET a.n=a.n+a.delta+g.amount WHERE a.id={lookup} AND a.seq BETWEEN 20 AND 22"
        )

        def try_insert(key):
            return rolled_back_outcome(probe, f"INSERT INTO gate VALUES ({key},0)")

        for level, command, blocking in (
            ("READ UNCOMMITTED", projected, 1),
            ("READ COMMITTED", direct, 1),
            ("REPEATABLE READ", direct, TIMEOUT),
            ("SERIALIZABLE", projected, TIMEOUT),
        ):
            execute(writer, "SET SESSION TRANSACTION ISOLATION LEVEL " + level)
            execute(writer, "START TRANSACTION")
            expect(f"{level} unsigned lookup", outcome(writer, command), 0)
            expect(
                f"{level} unsigned interior and exterior inserts",
                (try_insert(lookup), try_insert(lower - 1)),
                (blocking, 1),
            )
            expect(
                f"{level} unsigned move from UINT64_MAX",
                rolled_back_outcome(
                    probe, f"UPDATE gate SET id={upper - 1} WHERE id={upper}"
                ),
                blocking,
            )
            execute(writer, "ROLLBACK")
            expect(f"{level} released unsigned gap", try_insert(lookup), 1)

        execute(writer, "SET SESSION TRANSACTION ISOLATION LEVEL REPEATABLE READ")
        execute(owner, "START TRANSACTION")
        execute(owner, f"INSERT INTO gate VALUES ({lower + 2},0)")
        execute(writer, "START TRANSACTION")
        expect(
            "unsigned lookup beside pending predecessor", outcome(writer, projected), 0
        )
        expect("pending unsigned predecessor bounds the gap", try_insert(lower + 1), 1)
        execute(owner, "ROLLBACK")
        expect(
            "unsigned gap widens after predecessor rollback",
            try_insert(lower + 1),
            TIMEOUT,
        )
        execute(writer, "ROLLBACK")

        execute(writer, "START TRANSACTION")
        expect(
            "conflicting unsigned bounds",
            outcome(writer, projected + f" AND g.gate_id={upper}"),
            0,
        )
        expect("conflicting unsigned bounds claim no gap", try_insert(lookup), 1)
        execute(writer, "ROLLBACK")

        execute(admin, f"INSERT INTO gate VALUES ({lookup},1)")
        execute(writer, "START TRANSACTION")
        expect("unsigned lookup matches a visible row", outcome(writer, projected), 1)
        expect(
            "unsigned lookup reads the joined row",
            scalar(writer, "SELECT n FROM parent"),
            14,
        )
        expect(
            "unsigned lookup retains a shared row lock",
            rolled_back_outcome(probe, f"UPDATE gate SET delta=2 WHERE id={lookup}"),
            TIMEOUT,
        )
        execute(writer, "ROLLBACK")

    with fixture(port, schema, mysql) as (_, admin, reader, _, probe):
        execute(admin, "INSERT INTO gate VALUES (1,7),(5,13)")
        for level, start, finish, blocking in (
            ("REPEATABLE READ", "BEGIN", ("ROLLBACK",), 1),
            ("SERIALIZABLE", "BEGIN", ("ROLLBACK",), TIMEOUT),
            ("SERIALIZABLE", "START TRANSACTION READ ONLY", ("ROLLBACK",), TIMEOUT),
            (
                "SERIALIZABLE",
                "XA START 'point_read'",
                ("XA END 'point_read'", "XA ROLLBACK 'point_read'"),
                TIMEOUT,
            ),
            ("SERIALIZABLE", None, (), 1),
        ):
            execute(reader, "SET SESSION TRANSACTION ISOLATION LEVEL " + level)
            if start is not None:
                execute(reader, start)
            label = f"{level} {start or 'autocommit'}"
            expect(
                f"{label} exact row",
                scalar(reader, "SELECT delta FROM gate WHERE id=1"),
                7,
            )
            expect(
                f"{label} missing key",
                outcome(reader, "SELECT delta FROM gate WHERE id=3"),
                0,
            )
            expect(
                f"{label} point read locks",
                (
                    rolled_back_outcome(probe, "UPDATE gate SET delta=9 WHERE id=1"),
                    rolled_back_outcome(probe, "INSERT INTO gate VALUES (3,0)"),
                    rolled_back_outcome(probe, "UPDATE gate SET delta=9 WHERE id=5"),
                ),
                (blocking, blocking, 1),
            )
            for sql in finish:
                execute(reader, sql)

    with fixture(port, schema, mysql) as (_, admin, reader, _, probe):
        execute(admin, "INSERT INTO gate VALUES (10,1),(20,2),(30,3),(40,4)")
        execute(reader, "SET SESSION TRANSACTION ISOLATION LEVEL SERIALIZABLE")
        for predicate, selected, row_locks, gap_locks in (
            ("id BETWEEN 10 AND 30", (10, 20, 30), (10, 20, 30), (15, 25)),
            ("id BETWEEN 12 AND 28", (20,), (20,), (15, 25)),
            ("id BETWEEN 12 AND 18", (), (), (15,)),
            ("id > 10 AND id < 30", (20,), (20,), (15, 25)),
            ("id >= 10 AND id < 30", (10, 20), (10, 20), (15, 25)),
            ("id > 30", (40,), (40,), (35, 45)),
            ("id < 20", (10,), (10,), (5, 15)),
            ("id BETWEEN 30 AND 10", (), (), ()),
        ):
            execute(reader, "BEGIN")
            with reader.cursor() as cursor:
                cursor.execute(
                    "SELECT id FROM gate FORCE INDEX (PRIMARY) WHERE " + predicate
                )
                expect(
                    predicate + " selected rows",
                    tuple(sorted(row[0] for row in cursor.fetchall())),
                    selected,
                )
            expect(
                predicate + " row locks",
                tuple(
                    rolled_back_outcome(
                        probe, f"SELECT id FROM gate WHERE id={key} FOR UPDATE NOWAIT"
                    )
                    for key in (10, 20, 30, 40)
                ),
                tuple(3572 if key in row_locks else 1 for key in (10, 20, 30, 40)),
            )
            expect(
                predicate + " gap locks",
                tuple(
                    rolled_back_outcome(probe, f"INSERT INTO gate VALUES ({key},0)")
                    for key in (5, 15, 25, 35, 45)
                ),
                tuple(
                    TIMEOUT if key in gap_locks else 1 for key in (5, 15, 25, 35, 45)
                ),
            )
            execute(reader, "ROLLBACK")

    descending = "CREATE TABLE gate(id INT,PRIMARY KEY(id DESC))"
    composite_rows = "INSERT INTO gate VALUES (1,40),(2,10),(2,20),(2,30),(2,40),(4,10)"
    for label, setup, query, selected, inserts in (
        (
            "descending end gap",
            (descending, "INSERT INTO gate VALUES (10),(20),(30),(40)"),
            "SELECT id FROM gate FORCE INDEX (PRIMARY) WHERE id BETWEEN 10 AND 30",
            ((10,), (20,), (30,)),
            (("(5)", TIMEOUT), ("(35)", 1)),
        ),
        (
            "descending end followed by a record",
            (descending, "INSERT INTO gate VALUES (5),(10),(20),(30),(40)"),
            "SELECT id FROM gate FORCE INDEX (PRIMARY) WHERE id BETWEEN 10 AND 30",
            ((10,), (20,), (30,)),
            (("(7)", 1), ("(15)", TIMEOUT)),
        ),
        (
            "mixed-direction prefix",
            ("CREATE TABLE gate(a INT,b INT,PRIMARY KEY(a,b DESC))", composite_rows),
            "SELECT a,b FROM gate FORCE INDEX (PRIMARY) WHERE a=2",
            ((2, 10), (2, 20), (2, 30), (2, 40)),
            (("(2,5)", TIMEOUT), ("(3,10)", TIMEOUT), ("(1,45)", 1)),
        ),
        (
            "missing descending prefix",
            ("CREATE TABLE gate(a INT,b INT,PRIMARY KEY(a DESC,b))", composite_rows),
            "SELECT a,b FROM gate FORCE INDEX (PRIMARY) WHERE a=3",
            (),
            (("(2,5)", TIMEOUT), ("(3,10)", TIMEOUT), ("(2,45)", 1)),
        ),
    ):
        with fixture(port, schema, mysql, setup) as (_, _, reader, _, probe):
            execute(reader, "SET SESSION TRANSACTION ISOLATION LEVEL SERIALIZABLE")
            execute(reader, "BEGIN")
            with reader.cursor() as cursor:
                cursor.execute(query)
                expect(
                    label + " selected rows", tuple(sorted(cursor.fetchall())), selected
                )
            for values, expected in inserts:
                sql = f"INSERT INTO gate VALUES {values}"
                expect(label + " " + sql, rolled_back_outcome(probe, sql), expected)
            execute(reader, "ROLLBACK")

    limited_points = (
        ("SELECT id FROM gate WHERE id=20 LIMIT 1", ((20,),), 3572, 1, None),
        (
            "SELECT id FROM gate WHERE id=25 ORDER BY id DESC LIMIT 1",
            (),
            1,
            TIMEOUT,
            None,
        ),
        (
            "SELECT id FROM gate WHERE id=20 ORDER BY n LIMIT 1 OFFSET 1",
            (),
            3572,
            1,
            None,
        ),
        ("SELECT id FROM gate WHERE id=20 LIMIT 0", (), 1, 1, None),
        ("SELECT id FROM gate WHERE id=25 LIMIT 0", (), 1, 1, None),
        ("SELECT SQL_CALC_FOUND_ROWS id FROM gate WHERE id=20 LIMIT 0", (), 3572, 1, 1),
        (
            "SELECT SQL_CALC_FOUND_ROWS id FROM gate WHERE id=25 LIMIT 0",
            (),
            1,
            TIMEOUT,
            0,
        ),
    )
    setup = (
        "CREATE TABLE gate(id INT PRIMARY KEY,n INT)",
        "INSERT INTO gate VALUES (10,1),(20,2),(30,3)",
        "CREATE PROCEDURE pick(IN id INT) SELECT gate.id FROM gate WHERE id=20 LIMIT 1",
    )
    with fixture(port, schema, mysql, setup) as (_, _, reader, _, probe):
        execute(reader, "SET SESSION TRANSACTION ISOLATION LEVEL SERIALIZABLE")
        for sql, rows, row_lock, gap_lock, found in limited_points:
            execute(reader, "BEGIN")
            with reader.cursor() as cursor:
                cursor.execute(sql)
                expect(sql + " rows", tuple(cursor.fetchall()), rows)
            if found is not None:
                expect(
                    sql + " found rows", scalar(reader, "SELECT FOUND_ROWS()"), found
                )
            expect(
                sql + " row lock",
                rolled_back_outcome(
                    probe, "SELECT id FROM gate WHERE id=20 FOR UPDATE NOWAIT"
                ),
                row_lock,
            )
            expect(
                sql + " gap lock",
                rolled_back_outcome(probe, "INSERT INTO gate VALUES (25,0)"),
                gap_lock,
            )
            execute(reader, "ROLLBACK")

        execute(reader, "BEGIN")
        with reader.cursor() as cursor:
            cursor.execute("CALL pick(99)")
            expect(
                "the shadowing routine parameter filters the row", cursor.fetchall(), ()
            )
            while cursor.nextset():
                cursor.fetchall()
        expect(
            "a shadowed key stays writable",
            rolled_back_outcome(
                probe, "SELECT id FROM gate WHERE id=20 FOR UPDATE NOWAIT"
            ),
            1,
        )
        execute(reader, "ROLLBACK")

    ascending_range = (
        "CREATE TABLE gate(id INT PRIMARY KEY)",
        "INSERT INTO gate VALUES (10),(20),(30),(40)",
    )
    composite_range = (
        "CREATE TABLE gate(a INT,b INT,PRIMARY KEY(a,b DESC))",
        composite_rows,
    )
    for label, setup, query, rows, row_probes, inserts in (
        (
            "limited offset",
            ascending_range,
            "SELECT id FROM gate FORCE INDEX(PRIMARY) WHERE id BETWEEN 12 AND 39 ORDER BY id LIMIT 1 OFFSET 1",
            ((30,),),
            (("id=20", 3572), ("id=30", 3572), ("id=40", 1)),
            (("(11)", TIMEOUT), ("(25)", TIMEOUT), ("(35)", 1)),
        ),
        (
            "limited exact start",
            ascending_range,
            "SELECT id FROM gate FORCE INDEX(PRIMARY) WHERE id BETWEEN 20 AND 39 ORDER BY 1 LIMIT 1",
            ((20,),),
            (("id=20", 3572), ("id=30", 1)),
            (("(15)", 1), ("(25)", 1)),
        ),
        (
            "limited exhaustion",
            ascending_range,
            "SELECT id FROM gate FORCE INDEX(PRIMARY) WHERE id BETWEEN 12 AND 28 ORDER BY id LIMIT 5",
            ((20,),),
            (("id=20", 3572), ("id=30", 1)),
            (("(11)", TIMEOUT), ("(29)", TIMEOUT)),
        ),
        (
            "limited descending end",
            (descending, ascending_range[1]),
            "SELECT id FROM gate FORCE INDEX(PRIMARY) WHERE id BETWEEN 10 AND 39 ORDER BY id DESC LIMIT 3",
            ((30,), (20,), (10,)),
            (("id=10", 3572), ("id=40", 1)),
            (("(35)", TIMEOUT), ("(5)", 1)),
        ),
        (
            "limited composite prefix",
            ("CREATE TABLE gate(a INT,b INT,PRIMARY KEY(a,b DESC))", composite_rows),
            "SELECT b AS newest FROM gate FORCE INDEX(PRIMARY) WHERE a=2 ORDER BY newest DESC LIMIT 2",
            ((40,), (30,)),
            (("a=2 AND b=30", 3572), ("a=2 AND b=20", 1)),
            (("(2,45)", TIMEOUT), ("(2,35)", TIMEOUT), ("(2,25)", 1)),
        ),
        (
            "reverse inclusive seek",
            ascending_range,
            "SELECT id FROM gate FORCE INDEX(PRIMARY) WHERE id BETWEEN 20 AND 30 ORDER BY id DESC LIMIT 1",
            ((30,),),
            (("id=30", 3572), ("id=20", 1), ("id=40", 1)),
            (("(25)", TIMEOUT), ("(35)", TIMEOUT), ("(15)", 1)),
        ),
        (
            "reverse offset",
            ascending_range,
            "SELECT id FROM gate FORCE INDEX(PRIMARY) WHERE id BETWEEN 12 AND 39 ORDER BY id DESC LIMIT 1 OFFSET 1",
            ((20,),),
            (("id=20", 3572), ("id=30", 3572), ("id=10", 1)),
            (("(15)", TIMEOUT), ("(35)", TIMEOUT), ("(45)", 1)),
        ),
        (
            "reverse empty range",
            ascending_range,
            "SELECT id FROM gate FORCE INDEX(PRIMARY) WHERE id BETWEEN 12 AND 19 ORDER BY id DESC LIMIT 1",
            (),
            (("id=10", 3572), ("id=20", 1)),
            (("(5)", TIMEOUT), ("(15)", TIMEOUT), ("(25)", 1)),
        ),
        (
            "reverse exhaustion",
            ascending_range,
            "SELECT id FROM gate FORCE INDEX(PRIMARY) WHERE id BETWEEN 20 AND 30 ORDER BY id DESC LIMIT 5",
            ((30,), (20,)),
            (("id=10", 3572), ("id=40", 1)),
            (("(5)", TIMEOUT), ("(35)", TIMEOUT), ("(45)", 1)),
        ),
        (
            "reverse descending index",
            (descending, ascending_range[1]),
            "SELECT id FROM gate FORCE INDEX(PRIMARY) WHERE id BETWEEN 12 AND 39 ORDER BY id ASC LIMIT 1",
            ((20,),),
            (("id=20", 3572), ("id=10", 1), ("id=30", 1)),
            (("(15)", TIMEOUT), ("(25)", TIMEOUT), ("(35)", 1)),
        ),
        (
            "reverse open seek",
            ascending_range,
            "SELECT id FROM gate FORCE INDEX(PRIMARY) WHERE id>=20 ORDER BY id DESC LIMIT 1",
            ((40,),),
            (("id=40", 3572), ("id=30", 1)),
            (("(35)", TIMEOUT), ("(45)", TIMEOUT), ("(25)", 1)),
        ),
        (
            "reverse seek before first record",
            ascending_range,
            "SELECT id FROM gate FORCE INDEX(PRIMARY) WHERE id<10 ORDER BY id DESC LIMIT 1",
            (),
            (("id=10", 1),),
            (("(5)", TIMEOUT), ("(15)", 1)),
        ),
        (
            "reverse composite prefix",
            composite_range,
            "SELECT b FROM gate FORCE INDEX(PRIMARY) WHERE a=2 ORDER BY b ASC LIMIT 1",
            ((10,),),
            (("a=2 AND b=10", 3572), ("a=2 AND b=20", 1)),
            (("(2,15)", TIMEOUT), ("(3,10)", TIMEOUT), ("(2,25)", 1)),
        ),
        (
            "reverse exhausted composite prefix",
            composite_range,
            "SELECT b FROM gate FORCE INDEX(PRIMARY) WHERE a=2 ORDER BY a DESC,b ASC LIMIT 8",
            ((10,), (20,), (30,), (40,)),
            (("a=1 AND b=40", 3572), ("a=4 AND b=10", 1)),
            (("(1,45)", TIMEOUT), ("(3,10)", TIMEOUT), ("(4,5)", 1)),
        ),
        (
            "reverse absent composite prefix",
            composite_range,
            "SELECT b FROM gate FORCE INDEX(PRIMARY) WHERE a=3 ORDER BY b ASC LIMIT 1",
            (),
            (("a=2 AND b=10", 1), ("a=4 AND b=10", 1)),
            (("(2,15)", TIMEOUT), ("(3,10)", TIMEOUT), ("(2,25)", 1)),
        ),
        (
            "reverse absent prefix before the first record",
            composite_range,
            "SELECT b FROM gate FORCE INDEX(PRIMARY) WHERE a=0 ORDER BY b ASC LIMIT 1",
            (),
            (("a=1 AND b=40", 1),),
            (("(0,10)", TIMEOUT), ("(1,35)", 1)),
        ),
        (
            "reverse two-column prefix with offset",
            (
                "CREATE TABLE gate(a INT,b INT,c INT,PRIMARY KEY(a,b DESC,c DESC))",
                "INSERT INTO gate VALUES (2,10,10),(2,20,10),(2,20,20),(2,30,30),(2,40,40)",
            ),
            "SELECT c FROM gate FORCE INDEX(PRIMARY) WHERE a=2 AND b=20 ORDER BY c ASC LIMIT 1 OFFSET 1",
            ((20,),),
            (
                ("a=2 AND b=20 AND c=10", 3572),
                ("a=2 AND b=20 AND c=20", 3572),
                ("a=2 AND b=30 AND c=30", 1),
            ),
            (("(2,25,1)", TIMEOUT), ("(2,15,1)", TIMEOUT), ("(2,35,1)", 1)),
        ),
        (
            "backward composite suffix range",
            ("CREATE TABLE gate(a INT,b INT,PRIMARY KEY(a,b))", composite_rows),
            "SELECT b FROM gate FORCE INDEX(PRIMARY) WHERE a=2 AND b BETWEEN 20 AND 30 ORDER BY b DESC LIMIT 1",
            ((30,),),
            (("a=2 AND b=20", 1), ("a=2 AND b=30", 3572)),
            (("(2,15)", 1), ("(2,25)", TIMEOUT), ("(2,35)", TIMEOUT)),
        ),
        (
            "empty backward composite suffix range",
            ("CREATE TABLE gate(a INT,b INT,PRIMARY KEY(a,b))", composite_rows),
            "SELECT b FROM gate FORCE INDEX(PRIMARY) WHERE a=3 AND b BETWEEN 20 AND 30 ORDER BY b DESC LIMIT 1",
            (),
            (("a=2 AND b=40", 3572), ("a=4 AND b=10", 1)),
            (("(2,35)", TIMEOUT), ("(3,25)", TIMEOUT), ("(2,25)", 1)),
        ),
        (
            "backward descending composite suffix range",
            (
                "CREATE TABLE gate(a INT,b INT,PRIMARY KEY(a DESC,b DESC))",
                composite_rows,
            ),
            "SELECT b FROM gate FORCE INDEX(PRIMARY) WHERE a=2 AND b BETWEEN 20 AND 30 ORDER BY b ASC LIMIT 1",
            ((20,),),
            (("a=2 AND b=20", 3572), ("a=2 AND b=30", 1)),
            (("(2,15)", TIMEOUT), ("(2,25)", TIMEOUT), ("(2,35)", 1)),
        ),
        (
            "filesort locks the complete composite suffix range",
            composite_range,
            "SELECT b FROM gate FORCE INDEX(PRIMARY) WHERE a=2 AND b BETWEEN 20 AND 30 ORDER BY b ASC LIMIT 1",
            ((20,),),
            (("a=2 AND b=20", 3572), ("a=2 AND b=30", 3572), ("a=2 AND b=40", 1)),
            (("(2,15)", 1), ("(2,25)", TIMEOUT), ("(2,35)", 1)),
        ),
        (
            "backward range skips an equality-bound key direction",
            (
                "CREATE TABLE gate(a INT,b INT,c INT,PRIMARY KEY(a,b DESC,c))",
                "INSERT INTO gate VALUES (2,10,5),(2,20,5),(2,20,10),(2,30,5)",
            ),
            "SELECT c FROM gate FORCE INDEX(PRIMARY) WHERE a=2 AND b=20 AND c BETWEEN 5 AND 10 ORDER BY c DESC LIMIT 1",
            ((10,),),
            (("a=2 AND b=20 AND c=5", 1), ("a=2 AND b=20 AND c=10", 3572)),
            (("(2,20,7)", TIMEOUT), ("(2,15,5)", TIMEOUT), ("(2,25,5)", 1)),
        ),
        (
            "filesort locks the complete leading composite range",
            composite_range,
            "SELECT b FROM gate FORCE INDEX(PRIMARY) WHERE a BETWEEN 2 AND 3 ORDER BY a DESC,b ASC LIMIT 1",
            ((10,),),
            (("a=2 AND b=10", 3572), ("a=2 AND b=40", 3572), ("a=4 AND b=10", 1)),
            (("(3,10)", TIMEOUT), ("(4,5)", 1)),
        ),
        (
            "equal BETWEEN bounds extend the equality prefix",
            (
                "CREATE TABLE gate(a INT,b INT,c INT,PRIMARY KEY(a,b,c DESC))",
                "INSERT INTO gate VALUES (2,10,5),(2,20,5),(2,20,10),(2,30,5)",
            ),
            "SELECT c FROM gate FORCE INDEX(PRIMARY) WHERE a=2 AND b BETWEEN 20 AND 20 ORDER BY c ASC LIMIT 1",
            ((5,),),
            (("a=2 AND b=20 AND c=5", 3572), ("a=2 AND b=20 AND c=10", 1)),
            (("(2,20,7)", TIMEOUT), ("(2,25,5)", TIMEOUT), ("(2,15,5)", 1)),
        ),
    ):
        with fixture(port, schema, mysql, setup) as (_, _, reader, _, probe):
            execute(reader, "SET SESSION TRANSACTION ISOLATION LEVEL SERIALIZABLE")
            execute(reader, "BEGIN")
            with reader.cursor() as cursor:
                cursor.execute(query)
                expect(label + " rows", cursor.fetchall(), rows)
            for predicate, expected in row_probes:
                sql = f"SELECT * FROM gate WHERE {predicate} FOR UPDATE NOWAIT"
                expect(label + " " + sql, rolled_back_outcome(probe, sql), expected)
            for values, expected in inserts:
                sql = f"INSERT INTO gate VALUES {values}"
                expect(label + " " + sql, rolled_back_outcome(probe, sql), expected)
            execute(reader, "ROLLBACK")

    def check_limited_wait(label, setup, read_sql, first_key, later_key, expected):
        with fixture(port, schema, mysql, setup) as (
            _,
            later,
            reader,
            first,
            _,
        ):
            execute(reader, "SET SESSION TRANSACTION ISOLATION LEVEL SERIALIZABLE")
            for connection, key in ((later, later_key), (first, first_key)):
                execute(connection, "BEGIN")
                execute(
                    connection,
                    "INSERT INTO gate VALUES (" + ",".join(map(str, key)) + ")",
                )
            execute(reader, "BEGIN")
            dispatched = Event()

            def first_row():
                with reader.cursor() as cursor:
                    dispatched.set()
                    cursor.execute(read_sql)
                    return cursor.fetchall()

            with ThreadPoolExecutor(max_workers=1) as pool:
                result = pool.submit(first_row)
                try:
                    assert dispatched.wait(1)
                    try:
                        result.result(timeout=0.15)
                        waited = False
                    except TimeoutError:
                        waited = True
                    expect(
                        f"{label} limited read waits for the first pending key",
                        waited,
                        True,
                    )
                    execute(first, "COMMIT")
                    try:
                        rows = result.result(timeout=0.65)
                    except TimeoutError:
                        rows = "still waiting"
                    expect(
                        f"{label} later pending key leaves the read free to finish",
                        rows,
                        expected,
                    )
                finally:
                    execute(later, "ROLLBACK")
            execute(reader, "ROLLBACK")

    for direction, first_key, later_key in (("ASC", 15, 18), ("DESC", 37, 35)):
        check_limited_wait(
            direction,
            ascending_range,
            "SELECT id FROM gate FORCE INDEX(PRIMARY) "
            f"WHERE id BETWEEN 12 AND 39 ORDER BY id {direction} LIMIT 1",
            (first_key,),
            (later_key,),
            ((first_key,),),
        )
    check_limited_wait(
        "reverse composite",
        composite_range,
        "SELECT b FROM gate FORCE INDEX(PRIMARY) WHERE a=2 ORDER BY b ASC LIMIT 1",
        (2, 5),
        (2, 7),
        ((5,),),
    )
    check_limited_wait(
        "backward composite suffix range",
        ("CREATE TABLE gate(a INT,b INT,PRIMARY KEY(a,b))", composite_rows),
        "SELECT b FROM gate FORCE INDEX(PRIMARY) WHERE a=2 AND b BETWEEN 12 AND 39 ORDER BY b DESC LIMIT 1",
        (2, 38),
        (2, 35),
        ((38,),),
    )

    composite = "CREATE TABLE gate(a INT,b INT,PRIMARY KEY(a,b))"
    for label, old_table, old_row, replacement in (
        (
            "replace a single-column key",
            "CREATE TABLE gate(a INT PRIMARY KEY)",
            "(2)",
            ("DROP TABLE gate", composite),
        ),
        ("replace the same key", composite, "(2,35)", ("DROP TABLE gate", composite)),
        ("truncate the same key", composite, "(2,35)", ("TRUNCATE TABLE gate",)),
    ):
        setup = (
            old_table,
            f"INSERT INTO gate VALUES {old_row}",
            "CREATE TABLE sibling(id INT PRIMARY KEY)",
            "INSERT INTO sibling VALUES (1)",
        )
        with fixture(port, schema, mysql, setup) as (_, admin, older, reader, probe):
            execute(older, "SET SESSION TRANSACTION ISOLATION LEVEL REPEATABLE READ")
            execute(older, "BEGIN")
            expect(
                label + " older snapshot", scalar(older, "SELECT id FROM sibling"), 1
            )
            for sql in replacement:
                execute(admin, sql)
            execute(admin, "INSERT INTO gate VALUES (2,10),(2,20),(2,30),(2,40)")
            execute(reader, "SET SESSION TRANSACTION ISOLATION LEVEL SERIALIZABLE")
            execute(reader, "BEGIN")
            expect(
                label + " newest matching row",
                scalar(
                    reader,
                    "SELECT b FROM gate FORCE INDEX(PRIMARY) WHERE a=2 AND b BETWEEN 20 AND 30 ORDER BY b DESC LIMIT 1",
                ),
                30,
            )
            for value in (34, 36):
                expect(
                    f"{label} gap at {value}",
                    rolled_back_outcome(probe, f"INSERT INTO gate VALUES (2,{value})"),
                    TIMEOUT,
                )
            execute(reader, "ROLLBACK")
            execute(older, "ROLLBACK")

    metadata_setup = (
        "CREATE TABLE gate(id INT PRIMARY KEY,v INT)",
        "INSERT INTO gate VALUES (1,10)",
        "CREATE TABLE sibling(id INT PRIMARY KEY)",
    )
    for isolation in ("READ COMMITTED", "REPEATABLE READ", "SERIALIZABLE"):
        for ddl in (
            "DROP TABLE gate",
            "TRUNCATE TABLE gate",
            "ALTER TABLE gate ADD COLUMN pad INT",
            "RENAME TABLE gate TO moved",
        ):
            label = f"{isolation} {ddl}"
            with fixture(port, schema, mysql, metadata_setup) as (
                _,
                _,
                holder,
                ddl_session,
                independent,
            ):
                execute(ddl_session, "SET SESSION lock_wait_timeout=2")
                execute(holder, f"SET SESSION TRANSACTION ISOLATION LEVEL {isolation}")
                execute(holder, "BEGIN")
                expect(
                    label + " initial read", scalar(holder, "SELECT v FROM gate"), 10
                )
                sent = Event()

                def perform(connection, statement, dispatched):
                    dispatched.set()
                    return outcome(connection, statement)

                with ThreadPoolExecutor(1) as pool:
                    pending = pool.submit(perform, ddl_session, ddl, sent)
                    assert sent.wait(1)
                    try:
                        pending.result(0.15)
                        waited = False
                    except TimeoutError:
                        waited = True
                    try:
                        expect(label + " waits for metadata", waited, True)
                        expect(
                            label + " allows disjoint DDL",
                            outcome(independent, "DROP TABLE sibling"),
                            0,
                        )
                    finally:
                        execute(holder, "COMMIT")
                    expect(label + " completes on release", pending.result(2.5), 0)

    with fixture(port, schema, mysql, metadata_setup) as (_, _, holder, ddl, _):
        execute(ddl, "SET SESSION lock_wait_timeout=1")
        execute(holder, "SET SESSION autocommit=0")
        execute(holder, "UPDATE gate SET v=v+1 WHERE id=1")
        expect(
            "open write times out competing DDL",
            outcome(ddl, "DROP TABLE gate"),
            TIMEOUT,
        )
        execute(holder, "ROLLBACK")
        expect("rollback releases metadata", outcome(ddl, "DROP TABLE gate"), 0)

    def pending_statement(pool, connection, statement):
        dispatched = Event()

        def run():
            dispatched.set()
            return outcome(connection, statement)

        pending = pool.submit(run)
        assert dispatched.wait(1)
        return pending

    def blocked(pending):
        try:
            pending.result(0.15)
        except TimeoutError:
            return True
        return False

    with fixture(port, schema, mysql, metadata_setup) as (_, _, holder, ddl, late):
        execute(ddl, "SET SESSION lock_wait_timeout=3")
        execute(holder, "BEGIN")
        execute(holder, "SELECT v FROM gate")
        with ThreadPoolExecutor(2) as pool:
            dropping = pending_statement(pool, ddl, "DROP TABLE gate")
            try:
                expect("queued drop waits", blocked(dropping), True)
                expect("holder can reread", scalar(holder, "SELECT v FROM gate"), 10)
                reading = pending_statement(pool, late, "SELECT v FROM gate")
                expect("late reader waits behind drop", blocked(reading), True)
            finally:
                execute(holder, "COMMIT")
            expect("queued drop completes first", dropping.result(2), 0)
            expect("late reader sees the drop", reading.result(2), 1146)

    pair = (
        "CREATE TABLE a(id INT PRIMARY KEY)",
        "INSERT INTO a VALUES (1)",
        "CREATE TABLE z(id INT PRIMARY KEY)",
        "INSERT INTO z VALUES (1)",
        "CREATE TABLE audit(id INT PRIMARY KEY,v INT)",
        "INSERT INTO audit VALUES(1,10)",
    )
    for held, other, expected in (("a", "z", 1), ("z", "a", 1213)):
        with fixture(port, schema, mysql, pair) as (_, admin, holder, ddl, _):
            execute(ddl, "SET SESSION lock_wait_timeout=3")
            execute(holder, "SET SESSION lock_wait_timeout=2")
            execute(holder, "BEGIN")
            execute(holder, f"SELECT id FROM {held}")
            if expected == 1213:
                execute(holder, "UPDATE audit SET v=11 WHERE id=1")
            with ThreadPoolExecutor(1) as pool:
                dropping = pending_statement(pool, ddl, "DROP TABLE z,a")
                try:
                    expect(f"multi-table drop waits on {held}", blocked(dropping), True)
                    expect(
                        f"query {other} while drop waits on {held}",
                        outcome(holder, f"SELECT id FROM {other}"),
                        expected,
                    )
                    execute(
                        holder, "DO 0"
                    )  # An error packet does not refresh the server status.
                    expect(
                        "holder transaction status",
                        bool(holder.server_status & 1),
                        expected != 1213,
                    )
                    if expected == 1213:
                        expect(
                            "deadlock rolls back earlier writes",
                            scalar(admin, "SELECT v FROM audit WHERE id=1"),
                            10,
                        )
                        expect(
                            "victim releases its earlier claim", dropping.result(2), 0
                        )
                    else:
                        expect(
                            "holder keeps its earlier claim", blocked(dropping), True
                        )
                finally:
                    execute(holder, "COMMIT")
                expect("multi-table drop completes", dropping.result(2), 0)

    with fixture(port, schema, mysql, pair) as (_, _, holder, ddl, late):
        execute(ddl, "SET SESSION lock_wait_timeout=1")
        execute(holder, "BEGIN")
        execute(holder, "SELECT id FROM z")
        with ThreadPoolExecutor(2) as pool:
            dropping = pending_statement(pool, ddl, "DROP TABLE z,a")
            try:
                expect("drop waits on second table", blocked(dropping), True)
                reading = pending_statement(pool, late, "SELECT id FROM a")
                expect("first reservation holds the reader", blocked(reading), True)
                expect("multi-table drop times out", dropping.result(2), TIMEOUT)
                expect("timeout frees the first reservation", reading.result(2), 1)
            finally:
                execute(holder, "ROLLBACK")

    with fixture(port, schema, mysql, pair) as (_, _, holder, ddl, late):
        execute(holder, "BEGIN")
        execute(holder, "SELECT id FROM z")
        execute(ddl, "SET SESSION lock_wait_timeout=3")
        execute(late, "SET SESSION lock_wait_timeout=3")
        with ThreadPoolExecutor(2) as pool:
            dropping = pending_statement(pool, ddl, "DROP TABLE z,a")
            try:
                expect("drop reserves its first table", blocked(dropping), True)
                altering = pending_statement(
                    pool, late, "ALTER TABLE a ADD COLUMN v INT"
                )
                expect("later DDL waits on the reservation", blocked(altering), True)
            finally:
                execute(holder, "COMMIT")
            expect("drop keeps its earlier reservation", dropping.result(2), 0)
            expect("later DDL sees the missing table", altering.result(2), 1146)

    savepoint_setup = metadata_setup + ("INSERT INTO sibling VALUES (1)",)
    for earlier, later, drop in (
        (None, "SELECT id FROM gate", 0),
        (None, "SELECT id FROM gate FOR UPDATE", 0),
        (None, "UPDATE gate SET v=11 WHERE id=1", 0),
        ("SELECT 1", "SELECT id FROM gate FOR UPDATE", 0),
        ("SELECT id FROM sibling", "SELECT id FROM gate", 0),
        ("SELECT id FROM sibling LIMIT 0", "SELECT id FROM gate FOR UPDATE", TIMEOUT),
        (
            "SELECT id FROM sibling",
            "SELECT id FROM gate WHERE id=99 FOR UPDATE",
            TIMEOUT,
        ),
        ("SELECT id FROM sibling", "UPDATE gate SET v=11 WHERE id=99", TIMEOUT),
        ("SELECT id FROM sibling FOR UPDATE", "SELECT id FROM gate", TIMEOUT),
        ("UPDATE sibling SET id=id WHERE id=1", "SELECT id FROM gate", TIMEOUT),
        ("SELECT id FROM gate", "SELECT id FROM gate", TIMEOUT),
    ):
        with fixture(port, schema, mysql, savepoint_setup) as (
            _,
            _,
            holder,
            ddl,
            probe,
        ):
            execute(ddl, "SET SESSION lock_wait_timeout=1")
            execute(holder, "BEGIN")
            if earlier:
                execute(holder, earlier)
            execute(holder, "SAVEPOINT before_gate")
            execute(holder, later)
            execute(holder, "ROLLBACK TO SAVEPOINT before_gate")
            if earlier is None:
                expect(
                    f"{later} before storage joined leaves the row writable",
                    rolled_back_outcome(probe, "UPDATE gate SET v=12 WHERE id=1"),
                    1,
                )
            expect(
                f"savepoint after {earlier}, then {later}",
                outcome(ddl, "DROP TABLE gate"),
                drop,
            )
            execute(holder, "ROLLBACK")

    for earlier, expected in (
        (None, 22),
        ("SELECT 1", 22),
        ("SELECT id FROM sibling", 10),
    ):
        with fixture(port, schema, mysql, savepoint_setup) as (_, admin, holder, _, _):
            execute(holder, "BEGIN")
            if earlier:
                execute(holder, earlier)
            execute(holder, "SAVEPOINT before_read")
            expect(
                "first read before rollback", scalar(holder, "SELECT v FROM gate"), 10
            )
            execute(admin, "UPDATE gate SET v=22 WHERE id=1")
            execute(holder, "ROLLBACK TO SAVEPOINT before_read")
            expect(
                f"read view after savepoint with {earlier}",
                scalar(holder, "SELECT v FROM gate"),
                expected,
            )
            execute(holder, "ROLLBACK")

    failure_setup = (
        "CREATE TABLE gate(id INT PRIMARY KEY,v INT NOT NULL)",
        "INSERT INTO gate VALUES (1,10)",
        "CREATE TABLE sibling(id INT PRIMARY KEY)",
        "INSERT INTO sibling VALUES (1)",
        "CREATE VIEW visible AS SELECT id,v FROM gate",
    )
    for statement, error, ddl, waits in (
        ("SELECT missing FROM gate", 1054, "DROP TABLE gate", True),
        ("SELECT FROM gate", 1064, "DROP TABLE gate", False),
        ("SELECT * FROM gate,missing", 1146, "DROP TABLE gate", False),
        ("SELECT * FROM missing,gate", 1146, "DROP TABLE gate", False),
        (
            "SELECT * FROM gate JOIN gate ON gate.id=gate.id",
            1066,
            "DROP TABLE gate",
            False,
        ),
        (
            "SELECT id FROM gate JOIN sibling ON gate.id=sibling.id",
            1052,
            "DROP TABLE sibling",
            True,
        ),
        ("SELECT nonexistent_fn(id) FROM gate", 1305, "DROP TABLE gate", True),
        ("SELECT missing FROM visible", 1054, "DROP TABLE gate", True),
        ("SELECT missing FROM visible", 1054, "DROP VIEW visible", True),
        ("INSERT INTO gate VALUES (1,20)", 1062, "DROP TABLE gate", True),
        ("INSERT INTO gate VALUES (2,NULL)", 1048, "DROP TABLE gate", True),
        ("INSERT INTO gate VALUES (2)", 1136, "DROP TABLE gate", True),
        ("EXPLAIN SELECT missing FROM gate", 1054, "DROP TABLE gate", True),
    ):
        with fixture(port, schema, mysql, failure_setup) as (
            _,
            _,
            holder,
            ddl_session,
            _,
        ):
            execute(ddl_session, "SET SESSION lock_wait_timeout=1")
            execute(holder, "BEGIN")
            expect(statement, outcome(holder, statement), error)
            expect(
                f"{ddl} after {statement}",
                outcome(ddl_session, ddl),
                TIMEOUT if waits else 0,
            )
            execute(holder, "ROLLBACK")

    with fixture(port, schema, mysql, failure_setup) as (_, _, holder, ddl_session, _):
        execute(ddl_session, "SET SESSION lock_wait_timeout=1")
        execute(holder, "START TRANSACTION READ ONLY")
        expect(
            "read-only authorization error",
            outcome(holder, "UPDATE gate SET v=20 WHERE id=1"),
            1792,
        )
        expect(
            "authorization rejection leaves no metadata claim",
            outcome(ddl_session, "DROP TABLE gate"),
            0,
        )
        execute(holder, "ROLLBACK")

    with fixture(port, schema, mysql, failure_setup) as (_, _, holder, ddl_session, _):
        execute(ddl_session, "SET SESSION lock_wait_timeout=1")
        execute(holder, "BEGIN")
        execute(holder, "SAVEPOINT before_error")
        expect(
            "failed column binding", outcome(holder, "SELECT missing FROM gate"), 1054
        )
        expect(
            "failed binding holds metadata",
            outcome(ddl_session, "DROP TABLE gate"),
            TIMEOUT,
        )
        execute(holder, "ROLLBACK TO SAVEPOINT before_error")
        expect(
            "savepoint rollback releases failed statement metadata",
            outcome(ddl_session, "DROP TABLE gate"),
            0,
        )
        execute(holder, "ROLLBACK")

    for statement, error in (
        ("SELECT v FROM gate WHERE id=1 FOR UPDATE NOWAIT", 3572),
        ("UPDATE gate SET v=12 WHERE id=1", TIMEOUT),
    ):
        with fixture(port, schema, mysql, failure_setup) as (
            _,
            _,
            writer,
            holder,
            ddl_session,
        ):
            execute(ddl_session, "SET SESSION lock_wait_timeout=1")
            execute(writer, "BEGIN")
            execute(writer, "UPDATE gate SET v=11 WHERE id=1")
            execute(holder, "BEGIN")
            expect(statement, outcome(holder, statement), error)
            execute(writer, "COMMIT")
            expect(
                f"{statement} holds metadata after row lock failure",
                outcome(ddl_session, "DROP TABLE gate"),
                TIMEOUT,
            )
            execute(holder, "ROLLBACK")
            expect(
                "rollback releases failed row wait metadata",
                outcome(ddl_session, "DROP TABLE gate"),
                0,
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
