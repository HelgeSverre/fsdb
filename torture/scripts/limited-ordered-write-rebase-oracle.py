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

                print(f"MySQL {version}: {cases} ordered LIMIT 1/2 writes waited and reselected")
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
