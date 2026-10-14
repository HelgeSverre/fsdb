"""Check CTE-joined write waits against a disposable native MySQL 8.4.11."""

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


def execute(connection, sql):
    with connection.cursor() as cursor:
        cursor.execute(sql)
        return cursor.fetchall() if sql.startswith("SELECT") else cursor.rowcount


def verify_wait(owner, waiter, owner_sql, waiter_sql, expected_rows):
    execute(owner, "BEGIN")
    assert execute(owner, owner_sql) == 1
    execute(waiter, "BEGIN")
    result = []
    waiting = threading.Thread(target=lambda: result.append(execute(waiter, waiter_sql)))
    waiting.start()
    time.sleep(0.2)
    assert waiting.is_alive(), "CTE writer did not wait for the target row"
    execute(owner, "COMMIT")
    waiting.join(timeout=5)
    assert not waiting.is_alive(), "CTE writer did not resume"
    assert result == [1], result
    execute(waiter, "COMMIT")
    assert execute(owner, "SELECT id,n FROM probe.a ORDER BY id") == expected_rows


def main():
    with tempfile.TemporaryDirectory(prefix="fsdb-cte-write-oracle-") as directory:
        root = pathlib.Path(directory)
        data = root / "mysql-data"
        data.mkdir()
        socket = str(root / "mysql.sock")
        server = owner = waiter = None
        with (root / "mysql.log").open("w") as log:
            try:
                subprocess.run(
                    ["mysqld", "--no-defaults", "--initialize-insecure",
                     f"--datadir={data}", "--innodb-redo-log-capacity=64M"],
                    check=True, stdout=log, stderr=subprocess.STDOUT,
                )
                server = subprocess.Popen(
                    ["mysqld", "--no-defaults", f"--datadir={data}",
                     "--skip-networking", f"--socket={socket}",
                     f"--pid-file={root / 'mysql.pid'}", "--mysqlx=0",
                     "--innodb-buffer-pool-size=64M", "--innodb-redo-log-capacity=64M"],
                    stdout=log, stderr=subprocess.STDOUT,
                )
                owner = connect(socket)
                waiter = connect(socket)
                version = execute(owner, "SELECT VERSION()")[0][0]
                assert version.startswith("8.4.11"), version
                execute(owner, "CREATE DATABASE probe")
                execute(owner, "CREATE TABLE probe.a(id INT PRIMARY KEY,n INT)")
                execute(owner, "INSERT INTO probe.a VALUES(1,0),(2,0)")
                execute(owner, "USE probe")
                execute(waiter, "USE probe")

                verify_wait(
                    owner, waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT 1 AS id) UPDATE a JOIN c ON c.id=a.id SET a.n=a.n+1 WHERE a.id=1",
                    ((1, 2), (2, 0)),
                )
                verify_wait(
                    owner, waiter,
                    "UPDATE a SET n=n+1 WHERE id=1",
                    "WITH c AS (SELECT 1 AS id) UPDATE a JOIN c ON c.id=a.id SET n=n+1 WHERE a.id=1",
                    ((1, 4), (2, 0)),
                )
                verify_wait(
                    owner, waiter,
                    "UPDATE a SET n=n+1 WHERE id=2",
                    "WITH c AS (SELECT 2 AS id) DELETE a FROM a JOIN c ON c.id=a.id WHERE a.id=2",
                    ((1, 4),),
                )
                try:
                    execute(owner, "WITH c AS (SELECT 1 AS id,9 AS n) UPDATE a JOIN c ON c.id=a.id SET n=n+1 WHERE a.id=1")
                except pymysql.MySQLError as error:
                    assert error.args[0] == 1052, error
                else:
                    raise AssertionError("ambiguous CTE assignment was accepted")
                print("MySQL 8.4.11: CTE-joined UPDATE and DELETE waited and committed")
            except Exception:
                print((root / "mysql.log").read_text())
                raise
            finally:
                if waiter:
                    waiter.close()
                if owner:
                    owner.close()
                if server and server.poll() is None:
                    subprocess.run(
                        ["mysqladmin", "--no-defaults", f"--socket={socket}",
                         "-uroot", "shutdown"],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    )
                    server.wait(timeout=20)


if __name__ == "__main__":
    main()
