"""MySQL 8.4 oracle for an UPDATE through a view join followed by another join."""

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
    with tempfile.TemporaryDirectory(prefix="fsdb-nested-view-update-") as directory:
        root = pathlib.Path(directory)
        data = root / "mysql-data"
        socket = str(root / "mysql.sock")
        connections = []
        server = None
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
                owner, waiter, reader = (connect(socket) for _ in range(3))
                connections = [owner, waiter, reader]
                with owner.cursor() as cursor:
                    cursor.execute("SELECT VERSION()")
                    version = cursor.fetchone()[0]
                    assert version.startswith("8.4.11"), version
                    for sql in [
                        "CREATE DATABASE probe", "USE probe",
                        "CREATE TABLE t(id INT PRIMARY KEY,n INT,m INT DEFAULT 0)",
                        "CREATE TABLE l(id INT PRIMARY KEY)",
                        "CREATE TABLE x(id INT PRIMARY KEY)",
                        "CREATE TABLE y(id INT PRIMARY KEY)",
                        "INSERT INTO t(id,n) VALUES(1,0)", "INSERT INTO l VALUES(1)",
                        "INSERT INTO x VALUES(1)", "INSERT INTO y VALUES(1)",
                    ]:
                        cursor.execute(sql)
                waiter.select_db("probe")
                reader.select_db("probe")

                for (from_clause, assignment), view_position in itertools.product(
                    (
                        ("t JOIN l ON l.id=t.id", "v.n=v.n+1"),
                        ("l JOIN t ON l.id=t.id", "v.n=v.n+1"),
                        ("l JOIN t ON l.id=t.id", "n=n+1"),
                    ),
                    (1, 2, 3),
                ):
                    def joined_source(view_name):
                        if view_position == 1:
                            return f"{view_name} JOIN x ON x.id={view_name}.id"
                        if view_position == 2:
                            return f"x JOIN {view_name} ON x.id={view_name}.id"
                        return (
                            f"x JOIN y ON y.id=x.id JOIN {view_name} "
                            f"ON {view_name}.id=y.id"
                        )
                    with owner.cursor() as cursor:
                        cursor.execute("UPDATE t SET n=0 WHERE id=1")
                        cursor.execute("DROP VIEW IF EXISTS v")
                        cursor.execute("DROP VIEW IF EXISTS checked")
                        cursor.execute(
                            "CREATE VIEW v AS SELECT t.id,t.n FROM " + from_clause
                        )
                        cursor.execute("BEGIN")
                        cursor.execute("UPDATE t SET n=n+1 WHERE id=1")
                        assert cursor.rowcount == 1

                    started = threading.Event()
                    finished = threading.Event()
                    outcome = {}

                    def waiting_update():
                        try:
                            with waiter.cursor() as cursor:
                                cursor.execute("BEGIN")
                                started.set()
                                cursor.execute(
                                    "UPDATE " + joined_source("v") + " "
                                    "SET " + assignment + " WHERE v.id=1"
                                )
                                outcome["affected"] = cursor.rowcount
                                cursor.execute("COMMIT")
                        except Exception as error:
                            outcome["error"] = error
                        finally:
                            finished.set()

                    thread = threading.Thread(target=waiting_update, daemon=True)
                    thread.start()
                    assert started.wait(2), "waiting UPDATE did not start"
                    assert not finished.wait(0.2), "joined view UPDATE did not wait"
                    owner.commit()
                    assert finished.wait(5), "waiting UPDATE did not resume"
                    thread.join()
                    assert "error" not in outcome, outcome
                    assert outcome["affected"] == 1, outcome

                    with reader.cursor() as cursor:
                        cursor.execute("SELECT id,n FROM t")
                        assert cursor.fetchall() == ((1, 2),)
                        cursor.execute(
                            "CREATE VIEW checked AS SELECT t.id,t.n FROM "
                            + from_clause + " WITH CASCADED CHECK OPTION"
                        )
                        try:
                            cursor.execute(
                                "UPDATE " + joined_source("checked") + " "
                                "SET checked.id=2 WHERE checked.id=1"
                            )
                        except pymysql.MySQLError as error:
                            assert error.args[0] == 1369, error.args
                        else:
                            raise AssertionError("nested CHECK OPTION should reject id=2")
                        cursor.execute("SELECT id,n FROM t")
                        assert cursor.fetchall() == ((1, 2),)

                with reader.cursor() as cursor:
                    cursor.execute("ALTER TABLE x ADD COLUMN n INT DEFAULT 0")
                    cursor.execute("ALTER TABLE x ADD COLUMN m INT DEFAULT 0")
                    for source in (
                        "x JOIN v ON x.id=v.id",
                        "v JOIN x ON x.id=v.id",
                        "x JOIN y ON y.id=x.id JOIN v ON v.id=y.id",
                    ):
                        cursor.execute("UPDATE x SET n=0")
                        cursor.execute("UPDATE t SET n=2 WHERE id=1")
                        cursor.execute("UPDATE " + source + " SET x.n=x.n+1 WHERE v.id=1")
                        assert cursor.rowcount == 1, source
                        cursor.execute("SELECT n FROM x WHERE id=1")
                        assert cursor.fetchone() == (1,), source
                        cursor.execute("SELECT n FROM t WHERE id=1")
                        assert cursor.fetchone() == (2,), source
                        cursor.execute("UPDATE " + source + " SET x.n=x.n+1,v.n=v.n+1 WHERE v.id=1")
                        assert cursor.rowcount == 2, source
                        cursor.execute("SELECT n FROM x WHERE id=1")
                        assert cursor.fetchone() == (2,), source
                        cursor.execute("SELECT n FROM t WHERE id=1")
                        assert cursor.fetchone() == (3,), source
                        cursor.execute("UPDATE x SET m=0")
                        cursor.execute("UPDATE " + source + " SET m=m+1,v.n=v.n+1 WHERE v.id=1")
                        assert cursor.rowcount == 2, source
                        cursor.execute("SELECT m FROM x WHERE id=1")
                        assert cursor.fetchone() == (1,), source
                        cursor.execute("UPDATE t SET n=3 WHERE id=1")
                    for source in (
                        "v LEFT JOIN x ON x.id=v.id",
                        "x LEFT JOIN v ON v.id=x.id",
                        "v RIGHT JOIN x ON x.id=v.id",
                    ):
                        cursor.execute("UPDATE t SET n=3 WHERE id=1")
                        cursor.execute("UPDATE " + source + " SET v.n=v.n+1 WHERE v.id=1")
                        assert cursor.rowcount == 1, source
                        cursor.execute("SELECT n FROM t WHERE id=1")
                        assert cursor.fetchone() == (4,), source
                    cursor.execute("INSERT INTO x(id) VALUES(3)")
                    cursor.execute(
                        "UPDATE x LEFT JOIN v ON v.id=x.id SET v.n=v.n+1 WHERE x.id=3"
                    )
                    assert cursor.rowcount == 0
                    cursor.execute("UPDATE x SET n=0 WHERE id IN (1,3)")
                    cursor.execute("UPDATE t SET n=3 WHERE id=1")
                    cursor.execute(
                        "UPDATE x LEFT JOIN v ON v.id=x.id "
                        "SET x.n=x.n+1,v.n=v.n+1 WHERE x.id=1"
                    )
                    assert cursor.rowcount == 2
                    cursor.execute("SELECT n FROM x WHERE id=1")
                    assert cursor.fetchone() == (1,)
                    cursor.execute("SELECT n FROM t WHERE id=1")
                    assert cursor.fetchone() == (4,)
                    cursor.execute(
                        "UPDATE x LEFT JOIN v ON v.id=x.id "
                        "SET x.n=x.n+1,v.n=v.n+1 WHERE x.id=3"
                    )
                    assert cursor.rowcount == 1
                    cursor.execute("SELECT n FROM x WHERE id=3")
                    assert cursor.fetchone() == (1,)
                    cursor.execute(
                        "CREATE VIEW filtered_v AS SELECT t.id,t.n FROM t JOIN l "
                        "ON l.id=t.id WHERE t.n>0"
                    )
                    cursor.execute("UPDATE x SET n=0 WHERE id IN (1,3)")
                    cursor.execute("UPDATE t SET n=0 WHERE id=1")
                    for source in (
                        "x LEFT JOIN filtered_v v ON v.id=x.id",
                        "filtered_v v RIGHT JOIN x ON v.id=x.id",
                    ):
                        for id in (1, 3):
                            cursor.execute(f"UPDATE x SET n=0 WHERE id={id}")
                            cursor.execute(
                                "UPDATE " + source
                                + f" SET x.n=x.n+1,v.n=v.n+1 WHERE x.id={id}"
                            )
                            assert cursor.rowcount == 1, (source, id)
                            cursor.execute(f"SELECT n FROM x WHERE id={id}")
                            assert cursor.fetchone() == (1,), (source, id)
                    cursor.execute("SELECT n FROM t WHERE id=1")
                    assert cursor.fetchone() == (0,)
                    cursor.execute("UPDATE t SET n=10 WHERE id=1")
                    cursor.execute("UPDATE x SET n=0 WHERE id=1")
                    cursor.execute(
                        "UPDATE x LEFT JOIN filtered_v v ON v.id=x.id "
                        "SET x.n=x.n+1,v.n=v.n+1 WHERE x.id=1"
                    )
                    assert cursor.rowcount == 2
                    cursor.execute("UPDATE x SET n=2 WHERE id=1")
                    cursor.execute("UPDATE t SET n=3 WHERE id=1")
                    with owner.cursor() as owner_cursor:
                        owner_cursor.execute("BEGIN")
                        owner_cursor.execute("UPDATE t SET n=n+1 WHERE id=1")
                    multi_started = threading.Event()
                    multi_finished = threading.Event()
                    multi_outcome = {}

                    def waiting_multi_target():
                        try:
                            with waiter.cursor() as waiting_cursor:
                                waiting_cursor.execute("BEGIN")
                                multi_started.set()
                                waiting_cursor.execute(
                                    "UPDATE x JOIN v ON x.id=v.id "
                                    "SET x.n=x.n+1,v.n=v.n+1 WHERE v.id=1"
                                )
                                multi_outcome["affected"] = waiting_cursor.rowcount
                                waiting_cursor.execute("COMMIT")
                        except Exception as error:
                            multi_outcome["error"] = error
                        finally:
                            multi_finished.set()

                    multi_thread = threading.Thread(target=waiting_multi_target, daemon=True)
                    multi_thread.start()
                    assert multi_started.wait(2), "multi-target UPDATE did not start"
                    assert not multi_finished.wait(0.2), "multi-target UPDATE did not wait"
                    owner.commit()
                    assert multi_finished.wait(5), "multi-target UPDATE did not resume"
                    multi_thread.join()
                    assert multi_outcome == {"affected": 2}, multi_outcome
                    cursor.execute("SELECT n FROM x WHERE id=1")
                    assert cursor.fetchone() == (3,)
                    cursor.execute("SELECT n FROM t WHERE id=1")
                    assert cursor.fetchone() == (5,)
                    try:
                        cursor.execute(
                            "UPDATE x JOIN checked ON x.id=checked.id "
                            "SET x.n=x.n+1,checked.id=2 WHERE checked.id=1"
                        )
                    except pymysql.MySQLError as error:
                        assert error.args[0] == 1369, error.args
                    else:
                        raise AssertionError("multi-target CHECK OPTION should reject the write")
                    try:
                        cursor.execute(
                            "UPDATE x LEFT JOIN checked ON x.id=checked.id "
                            "SET x.n=x.n+1,checked.id=2 WHERE x.id=1"
                        )
                    except pymysql.MySQLError as error:
                        assert error.args[0] == 1369, error.args
                    else:
                        raise AssertionError("outer-join CHECK OPTION should reject the write")
                    cursor.execute("SELECT id,n FROM x WHERE id=1")
                    assert cursor.fetchone() == (1, 3)
                    cursor.execute("SELECT id,n FROM t WHERE id=1")
                    assert cursor.fetchone() == (1, 5)
                    cursor.execute("CREATE USER 'limited_view_definer'@'localhost'")
                    cursor.execute("GRANT SELECT,UPDATE ON probe.t TO 'limited_view_definer'@'localhost'")
                    cursor.execute("GRANT SELECT ON probe.l TO 'limited_view_definer'@'localhost'")
                    cursor.execute(
                        "CREATE DEFINER='limited_view_definer'@'localhost' SQL SECURITY DEFINER "
                        "VIEW limited_v AS SELECT t.id,t.n FROM t JOIN l ON l.id=t.id"
                    )
                    cursor.execute(
                        "UPDATE x JOIN limited_v ON x.id=limited_v.id "
                        "SET x.n=x.n+1,limited_v.n=limited_v.n+1 WHERE limited_v.id=1"
                    )
                    assert cursor.rowcount == 2
                    cursor.execute("SELECT n FROM x WHERE id=1")
                    assert cursor.fetchone() == (4,)
                    cursor.execute("SELECT n FROM t WHERE id=1")
                    assert cursor.fetchone() == (6,)
                    cursor.execute("ALTER TABLE x ADD COLUMN who VARCHAR(100)")
                    cursor.execute(
                        "UPDATE x JOIN limited_v ON x.id=limited_v.id "
                        "SET x.who=CURRENT_USER(),limited_v.n=limited_v.n+1 WHERE limited_v.id=1"
                    )
                    cursor.execute("SELECT who FROM x WHERE id=1")
                    assert cursor.fetchone() == ("root@localhost",)
                    cursor.execute("UPDATE t SET n=6 WHERE id=1")
                    cursor.execute("CREATE USER 'outer_view_definer'@'localhost'")
                    cursor.execute("GRANT SELECT,UPDATE ON probe.limited_v TO 'outer_view_definer'@'localhost'")
                    cursor.execute(
                        "CREATE DEFINER='outer_view_definer'@'localhost' SQL SECURITY DEFINER "
                        "VIEW outer_v AS SELECT id,n FROM limited_v"
                    )
                    cursor.execute(
                        "UPDATE x JOIN outer_v ON x.id=outer_v.id "
                        "SET x.n=x.n+1,outer_v.n=outer_v.n+1 WHERE outer_v.id=1"
                    )
                    assert cursor.rowcount == 2
                    cursor.execute("SELECT n FROM x WHERE id=1")
                    assert cursor.fetchone() == (5,)
                    cursor.execute("SELECT n FROM t WHERE id=1")
                    assert cursor.fetchone() == (7,)
                    for source in (
                        "v JOIN x ON x.id=v.id",
                        "x JOIN v ON x.id=v.id",
                        "x JOIN y ON y.id=x.id JOIN v ON v.id=y.id",
                    ):
                        try:
                            cursor.execute("UPDATE " + source + " SET n=n+1 WHERE v.id=1")
                        except pymysql.MySQLError as error:
                            assert error.args[0] == 1052, error.args
                        else:
                            raise AssertionError("shared bare n should be ambiguous")

                    cursor.execute("CREATE USER 'view_writer'@'localhost'")
                    cursor.execute("GRANT SELECT, UPDATE ON probe.v TO 'view_writer'@'localhost'")
                writer = pymysql.connect(
                    unix_socket=socket, user="view_writer", database="probe", autocommit=True
                )
                connections.append(writer)
                restricted = (
                    "UPDATE x JOIN y ON y.id=x.id JOIN v ON v.id=y.id "
                    "SET v.n=v.n+1 WHERE v.id=1"
                )
                for table in ("x", "y"):
                    try:
                        with writer.cursor() as cursor:
                            cursor.execute(restricted)
                    except pymysql.MySQLError as error:
                        assert error.args[0] in (1142, 1143), error.args
                    else:
                        raise AssertionError("the outer lookup requires SELECT privilege")
                    with reader.cursor() as cursor:
                        cursor.execute(
                            f"GRANT SELECT ON probe.{table} TO 'view_writer'@'localhost'"
                        )
                with writer.cursor() as cursor:
                    cursor.execute(restricted)
                    assert cursor.rowcount == 1
                    try:
                        cursor.execute(
                            "UPDATE x JOIN v ON x.id=v.id "
                            "SET x.n=x.n+1,v.n=v.n+1 WHERE v.id=1"
                        )
                    except pymysql.MySQLError as error:
                        assert error.args[0] == 1143, error.args
                    else:
                        raise AssertionError("outer physical target requires UPDATE privilege")

                with owner.cursor() as cursor:
                    cursor.execute("INSERT INTO t(id,n) VALUES(2,0)")
                    cursor.execute("INSERT INTO l VALUES(2)")
                    cursor.execute("INSERT INTO x(id) VALUES(2)")
                    cursor.execute("BEGIN")
                    cursor.execute("UPDATE t SET n=n+1 WHERE id=2")
                disjoint_done = threading.Event()
                disjoint_outcome = {}

                def disjoint_update():
                    try:
                        with waiter.cursor() as cursor:
                            cursor.execute("BEGIN")
                            cursor.execute(
                                "UPDATE v JOIN x ON x.id=v.id "
                                "SET v.n=v.n+1 WHERE v.id=1"
                            )
                            disjoint_outcome["affected"] = cursor.rowcount
                            cursor.execute("COMMIT")
                    except Exception as error:
                        disjoint_outcome["error"] = error
                    finally:
                        disjoint_done.set()

                disjoint_thread = threading.Thread(target=disjoint_update, daemon=True)
                disjoint_thread.start()
                completed_before_owner_commit = disjoint_done.wait(0.2)
                owner.commit()
                assert disjoint_done.wait(5), "disjoint UPDATE did not complete"
                disjoint_thread.join()
                assert completed_before_owner_commit, disjoint_outcome
                assert disjoint_outcome == {"affected": 1}, disjoint_outcome

                with reader.cursor() as cursor:
                    outer_sources = (
                        "x LEFT JOIN y ON y.id=x.id LEFT JOIN v ON v.id=y.id",
                        "x LEFT JOIN v ON v.id=x.id LEFT JOIN y ON y.id=v.id",
                    )
                    for source in outer_sources:
                        cursor.execute("UPDATE x SET n=0 WHERE id=1")
                        cursor.execute("UPDATE t SET n=0 WHERE id=1")
                        cursor.execute(
                            "UPDATE " + source
                            + " SET x.n=x.n+1,v.n=v.n+1 WHERE x.id=1"
                        )
                        assert cursor.rowcount == 2, source
                        cursor.execute("SELECT n FROM x WHERE id=1")
                        assert cursor.fetchone() == (1,), source
                        cursor.execute("SELECT n FROM t WHERE id=1")
                        assert cursor.fetchone() == (1,), source
                    cursor.execute("DELETE FROM y WHERE id=1")
                    cursor.execute("UPDATE x SET n=0 WHERE id=1")
                    cursor.execute("UPDATE t SET n=0 WHERE id=1")
                    cursor.execute(
                        "UPDATE x LEFT JOIN y ON y.id=x.id LEFT JOIN v ON v.id=y.id "
                        "SET x.n=x.n+1,v.n=v.n+1 WHERE x.id=1"
                    )
                    assert cursor.rowcount == 1
                    cursor.execute("SELECT n FROM x WHERE id=1")
                    assert cursor.fetchone() == (1,)
                    cursor.execute("SELECT n FROM t WHERE id=1")
                    assert cursor.fetchone() == (0,)
                    cursor.execute("INSERT INTO y VALUES(1)")
                    try:
                        cursor.execute(
                            "UPDATE x LEFT JOIN y ON y.id=x.id LEFT JOIN checked v ON v.id=y.id "
                            "SET x.n=x.n+1,v.id=2 WHERE x.id=1"
                        )
                    except pymysql.MySQLError as error:
                        assert error.args[0] == 1369, error.args
                    else:
                        raise AssertionError("three-source CHECK OPTION should reject id=2")
                    cursor.execute("SELECT n FROM x WHERE id=1")
                    assert cursor.fetchone() == (1,)
                    cursor.execute("SELECT id,n FROM t WHERE id=1")
                    assert cursor.fetchone() == (1, 0)

                    right_source = "x LEFT JOIN y ON y.id=x.id RIGHT JOIN v ON v.id=y.id"
                    cursor.execute("UPDATE x SET n=0 WHERE id=1")
                    cursor.execute("UPDATE t SET n=0 WHERE id=1")
                    cursor.execute(
                        "UPDATE " + right_source
                        + " SET x.n=x.n+1,v.n=v.n+1 WHERE v.id=1"
                    )
                    assert cursor.rowcount == 2, "matched right-preserving joined view"
                    cursor.execute("SELECT n FROM x WHERE id=1")
                    assert cursor.fetchone() == (1,)
                    cursor.execute("SELECT n FROM t WHERE id=1")
                    assert cursor.fetchone() == (1,)

                    cursor.execute("DELETE FROM y WHERE id=1")
                    cursor.execute("UPDATE x SET n=0 WHERE id=1")
                    cursor.execute("UPDATE t SET n=0 WHERE id=1")
                    cursor.execute(
                        "UPDATE " + right_source
                        + " SET x.n=x.n+1,v.n=v.n+1 WHERE v.id=1"
                    )
                    assert cursor.rowcount == 1, "missing preceding join keeps the view row"
                    cursor.execute("SELECT n FROM x WHERE id=1")
                    assert cursor.fetchone() == (0,)
                    cursor.execute("SELECT n FROM t WHERE id=1")
                    assert cursor.fetchone() == (1,)
                    cursor.execute("INSERT INTO y VALUES(1)")

                    cursor.execute("CREATE TABLE z(id INT PRIMARY KEY,n INT)")
                    extended_source = right_source + " LEFT JOIN z ON z.id=v.id"
                    cursor.execute("DELETE FROM z")
                    cursor.execute("INSERT INTO z VALUES(1,0)")
                    cursor.execute("UPDATE x SET n=0 WHERE id=1")
                    cursor.execute("UPDATE t SET n=0 WHERE id=1")
                    try:
                        cursor.execute(
                            "UPDATE x LEFT JOIN y ON y.id=x.id "
                            "RIGHT JOIN checked v ON v.id=y.id "
                            "LEFT JOIN z ON z.id=v.id "
                            "SET x.n=x.n+1,v.id=2,z.n=z.n+1 WHERE v.id=1"
                        )
                        raise AssertionError("four-source CHECK OPTION accepted a disappearing view row")
                    except pymysql.MySQLError as error:
                        assert error.args[0] == 1369, error
                    for table, column, expected in (("x", "n", 0), ("t", "id", 1),
                                                     ("z", "n", 0)):
                        cursor.execute(f"SELECT {column} FROM {table} WHERE id=1")
                        assert cursor.fetchone() == (expected,), (table, column)
                    for has_y, has_z in itertools.product((True, False), repeat=2):
                        cursor.execute("DELETE FROM y")
                        cursor.execute("DELETE FROM z")
                        if has_y:
                            cursor.execute("INSERT INTO y VALUES(1)")
                        if has_z:
                            cursor.execute("INSERT INTO z VALUES(1,0)")
                        cursor.execute("UPDATE x SET n=0 WHERE id=1")
                        cursor.execute("UPDATE t SET n=0 WHERE id=1")
                        cursor.execute(
                            "UPDATE " + extended_source + " SET x.n=x.n+1,v.n=v.n+1,z.n=z.n+1 WHERE v.id=1"
                        )
                        expected_count = 1 + int(has_y) + int(has_z)
                        assert cursor.rowcount == expected_count, (has_y, has_z, cursor.rowcount)
                        cursor.execute("SELECT n FROM x WHERE id=1")
                        assert cursor.fetchone() == (int(has_y),), (has_y, has_z)
                        cursor.execute("SELECT n FROM t WHERE id=1")
                        assert cursor.fetchone() == (1,), (has_y, has_z)
                        cursor.execute("SELECT n FROM z WHERE id=1")
                        assert cursor.fetchall() == (((1,),) if has_z else ()), (has_y, has_z)

                    for held_target in ("t", "x", "z"):
                        cursor.execute("DELETE FROM y")
                        cursor.execute("DELETE FROM z")
                        cursor.execute("INSERT INTO y VALUES(1)")
                        cursor.execute("INSERT INTO z VALUES(1,0)")
                        cursor.execute("UPDATE x SET n=0 WHERE id=1")
                        cursor.execute("UPDATE t SET n=0 WHERE id=1")
                        with owner.cursor() as owner_cursor:
                            owner_cursor.execute("BEGIN")
                            owner_cursor.execute(f"UPDATE {held_target} SET n=10 WHERE id=1")
                        started = threading.Event()
                        finished = threading.Event()
                        outcome = {}

                        def waiting_outer_update():
                            try:
                                with waiter.cursor() as waiting_cursor:
                                    waiting_cursor.execute("BEGIN")
                                    started.set()
                                    waiting_cursor.execute(
                                        "UPDATE " + extended_source
                                        + " SET x.n=x.n+1,v.n=v.n+1,z.n=z.n+1 WHERE v.id=1"
                                    )
                                    outcome["affected"] = waiting_cursor.rowcount
                                    waiting_cursor.execute("COMMIT")
                            except Exception as error:
                                outcome["error"] = error
                            finally:
                                finished.set()

                        thread = threading.Thread(target=waiting_outer_update, daemon=True)
                        thread.start()
                        assert started.wait(2), held_target
                        assert not finished.wait(0.2), held_target
                        owner.commit()
                        assert finished.wait(5), held_target
                        thread.join()
                        assert outcome == {"affected": 3}, (held_target, outcome)
                        for table in ("x", "t", "z"):
                            cursor.execute(f"SELECT n FROM {table} WHERE id=1")
                            expected = 11 if table == held_target else 1
                            assert cursor.fetchone() == (expected,), (held_target, table)

                    cursor.execute("UPDATE x SET n=0,m=0 WHERE id=1")
                    cursor.execute("UPDATE t SET n=0 WHERE id=1")
                    cursor.execute("UPDATE z SET n=0 WHERE id=1")
                    with owner.cursor() as owner_cursor:
                        owner_cursor.execute("BEGIN")
                        owner_cursor.execute("UPDATE x SET m=10 WHERE id=1")
                    started = threading.Event()
                    finished = threading.Event()
                    outcome = {}

                    def waiting_bare_outer_update():
                        try:
                            with waiter.cursor() as waiting_cursor:
                                waiting_cursor.execute("BEGIN")
                                started.set()
                                waiting_cursor.execute(
                                    "UPDATE " + extended_source
                                    + " SET m=m+1,v.n=v.n+1,z.n=z.n+1 WHERE v.id=1"
                                )
                                outcome["affected"] = waiting_cursor.rowcount
                                waiting_cursor.execute("COMMIT")
                        except Exception as error:
                            outcome["error"] = error
                        finally:
                            finished.set()

                    thread = threading.Thread(target=waiting_bare_outer_update, daemon=True)
                    thread.start()
                    assert started.wait(2), "bare outer assignment starts"
                    assert not finished.wait(0.2), "bare outer assignment waits for x"
                    owner.commit()
                    assert finished.wait(5), "bare outer assignment resumes"
                    thread.join()
                    assert outcome == {"affected": 3}, outcome
                    for table, column, expected in (("x", "m", 11), ("x", "n", 0),
                                                     ("t", "n", 1), ("z", "n", 1)):
                        cursor.execute(f"SELECT {column} FROM {table} WHERE id=1")
                        assert cursor.fetchone() == (expected,), (table, column)

                    cursor.execute("DELETE FROM z")
                    cursor.execute("INSERT INTO z VALUES(99,0)")
                    cursor.execute("UPDATE x SET n=0 WHERE id=1")
                    cursor.execute("UPDATE t SET n=0 WHERE id=1")
                    cursor.execute(
                        "UPDATE " + right_source + " RIGHT JOIN z ON z.id=v.id "
                        "SET x.n=x.n+1,v.n=v.n+1,z.n=z.n+1 WHERE z.id=99"
                    )
                    assert cursor.rowcount == 1, "the final right join preserves z without a view row"
                    cursor.execute("SELECT n FROM x WHERE id=1")
                    assert cursor.fetchone() == (0,)
                    cursor.execute("SELECT n FROM t WHERE id=1")
                    assert cursor.fetchone() == (0,)
                    cursor.execute("SELECT n FROM z WHERE id=99")
                    assert cursor.fetchone() == (1,)

                print(
                    f"MySQL {version}: view-first, second, and third positions and bare targets waited and rebased; "
                    "two-source outer joins preserved physical writes beside missing or filtered view rows; "
                    "nested CHECK OPTION returned 1369; shared bare n returned 1052; "
                    "third-position read grants were enforced; "
                    "two-target writes rebased and kept caller/definer grants separate; "
                    "disjoint rows did not wait; three-source outer joins preserved both left- and right-hand targets; "
                    "four-source right/left and right/right chains updated only matched physical targets; "
                    "contended view and outer targets waited and rebased all three assignments; "
                    "a bare outer-only column waited and rebased its physical target; "
                    "four-source CHECK OPTION rejected atomically"
                )
            finally:
                for connection in connections:
                    connection.close()
                if server and server.poll() is None:
                    subprocess.run(
                        ["mysqladmin", "--no-defaults", f"--socket={socket}", "-uroot", "shutdown"],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    )
                    server.wait(timeout=20)


if __name__ == "__main__":
    main()
