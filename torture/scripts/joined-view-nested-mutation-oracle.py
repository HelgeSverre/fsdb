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
                        "CREATE TABLE t(id INT PRIMARY KEY,n INT)",
                        "CREATE TABLE l(id INT PRIMARY KEY)",
                        "CREATE TABLE x(id INT PRIMARY KEY)",
                        "CREATE TABLE y(id INT PRIMARY KEY)",
                        "INSERT INTO t VALUES(1,0)", "INSERT INTO l VALUES(1)",
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

                with owner.cursor() as cursor:
                    cursor.execute("INSERT INTO t VALUES(2,0)")
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

                print(
                    f"MySQL {version}: view-first, second, and third positions and bare targets waited and rebased; "
                    "nested CHECK OPTION returned 1369; shared bare n returned 1052; "
                    "third-position read grants were enforced; "
                    "disjoint rows did not wait"
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
