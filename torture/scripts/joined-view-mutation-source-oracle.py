"""Probe MySQL 8.4 joined UPDATE through a view, including row-lock rebasing."""

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
    with tempfile.TemporaryDirectory(prefix="fsdb-joined-view-update-") as directory:
        root = pathlib.Path(directory)
        data = root / "mysql-data"
        socket = str(root / "mysql.sock")
        server = None
        connections = []

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
                        "INSERT INTO t VALUES(1,0)", "INSERT INTO l VALUES(1)",
                        "CREATE VIEW v AS SELECT id,n FROM t",
                    ]:
                        cursor.execute(sql)

                for connection in (waiter, reader):
                    connection.select_db("probe")

                with owner.cursor() as cursor:
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
                                "UPDATE v JOIN l ON l.id=v.id "
                                "SET v.n=v.n+1 WHERE v.id=1"
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
                assert not finished.wait(0.2), "joined view UPDATE did not wait for the base row"

                owner.commit()
                assert finished.wait(5), "waiting UPDATE did not resume"
                thread.join()
                assert "error" not in outcome, outcome
                assert outcome["affected"] == 1, outcome

                with reader.cursor() as cursor:
                    cursor.execute("SELECT n FROM t WHERE id=1")
                    assert cursor.fetchone() == (2,)

                    cursor.execute(
                        "CREATE VIEW renamed AS SELECT id AS public_id,n AS score FROM t"
                    )
                    cursor.execute(
                        "UPDATE renamed JOIN l ON l.id=renamed.public_id "
                        "SET renamed.score=renamed.score+1 WHERE renamed.public_id=1"
                    )
                    assert cursor.rowcount == 1
                    cursor.execute("SELECT n FROM t WHERE id=1")
                    assert cursor.fetchone() == (3,)

                    cursor.execute("ALTER TABLE l ADD COLUMN n INT DEFAULT 0")
                    try:
                        cursor.execute(
                            "UPDATE v JOIN l ON l.id=v.id "
                            "SET v.n=n+1 WHERE v.id=1"
                        )
                    except pymysql.MySQLError as error:
                        assert error.args[0] == 1052, error.args
                    else:
                        raise AssertionError("ambiguous bare n should fail")
                    cursor.execute("SELECT n FROM t WHERE id=1")
                    assert cursor.fetchone() == (3,)

                    cursor.execute(
                        "CREATE VIEW checked AS SELECT id,n FROM t WHERE n>=0 "
                        "WITH CASCADED CHECK OPTION"
                    )
                    try:
                        cursor.execute(
                            "UPDATE checked JOIN l ON l.id=checked.id "
                            "SET checked.n=-1 WHERE checked.id=1"
                        )
                    except pymysql.MySQLError as error:
                        assert error.args[0] == 1369, error.args
                    else:
                        raise AssertionError("joined view CHECK OPTION should reject n=-1")
                    cursor.execute("SELECT n FROM t WHERE id=1")
                    assert cursor.fetchone() == (3,)

                print(
                    f"MySQL {version}: joined view UPDATE waited and rebased; "
                    "renamed columns updated; CHECK OPTION rejected n=-1; bare n was ambiguous"
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
