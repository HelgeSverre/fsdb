"""Check ordered LIMIT write rebasing against a disposable MySQL 8.4 server."""

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
                for indexed in (False, True):
                    for verb in ("UPDATE", "DELETE"):
                        waiter = connect(socket)
                        try:
                            with owner.cursor() as cursor:
                                cursor.execute("DROP TABLE IF EXISTS target")
                                index = ", KEY ix_n(n)" if indexed else ""
                                cursor.execute(f"CREATE TABLE target(id INT PRIMARY KEY,n INT{index})")
                                cursor.execute("INSERT INTO target VALUES(1,0),(2,0)")
                                cursor.execute("BEGIN")
                                cursor.execute("UPDATE target SET n=-1 WHERE id=1")
                            with waiter.cursor() as cursor:
                                cursor.execute("USE probe")
                                cursor.execute("BEGIN")

                            statement = (
                                "UPDATE target SET n=n+1 WHERE n>=0 ORDER BY id LIMIT 1"
                                if verb == "UPDATE" else
                                "DELETE FROM target WHERE n>=0 ORDER BY id LIMIT 1"
                            )
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
                            assert outcome["affected"] == 1, (statement, outcome)
                            with owner.cursor() as cursor:
                                cursor.execute("SELECT id,n FROM target ORDER BY id")
                                actual = cursor.fetchall()
                            expected = ((1, -1), (2, 1)) if verb == "UPDATE" else ((1, -1),)
                            assert actual == expected, (statement, indexed, actual)
                            cases += 1
                        finally:
                            waiter.close()

                print(f"MySQL {version}: {cases} ordered LIMIT writes waited and reselected")
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
