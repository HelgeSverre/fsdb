"""Check pending ENUM index inserts in limited writes on MySQL 8.4.11.

Run with ``uv run --with pymysql python torture/scripts/keyless-enum-ordered-write-oracle.py``.
"""

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
    with tempfile.TemporaryDirectory(prefix="fsdb-enum-write-") as directory:
        root = pathlib.Path(directory)
        data = root / "data"
        data.mkdir()
        socket = str(root / "mysql.sock")
        with (root / "mysql.log").open("w") as log:
            subprocess.run(
                [
                    "mysqld",
                    "--no-defaults",
                    "--initialize-insecure",
                    f"--datadir={data}",
                    "--innodb-redo-log-capacity=64M",
                ],
                check=True,
                stdout=log,
                stderr=subprocess.STDOUT,
            )
            server = subprocess.Popen(
                [
                    "mysqld",
                    "--no-defaults",
                    f"--datadir={data}",
                    "--skip-networking",
                    f"--socket={socket}",
                    f"--pid-file={root / 'mysql.pid'}",
                    "--mysqlx=0",
                    "--innodb-buffer-pool-size=64M",
                    "--innodb-redo-log-capacity=64M",
                ],
                stdout=log,
                stderr=subprocess.STDOUT,
            )
            try:
                owner = connect(socket)
                with owner.cursor() as cursor:
                    cursor.execute("SELECT VERSION()")
                    assert cursor.fetchone()[0].startswith("8.4.11")
                    cursor.execute("CREATE DATABASE probe")
                    cursor.execute("USE probe")
                for key_definition in (
                    "PRIMARY KEY(rank_value)",
                    "KEY ix_rank(rank_value)",
                ):
                    for verb in ("UPDATE", "DELETE"):
                        for direction, pending, visible in (
                            ("ASC", "z", ("a", "m")),
                            ("DESC", "m", ("z", "a")),
                        ):
                            waiter = connect(socket)
                            try:
                                with owner.cursor() as cursor:
                                    cursor.execute("DROP TABLE IF EXISTS target")
                                    cursor.execute(
                                        f"CREATE TABLE target(rank_value ENUM('z','a','m'),n INT,{key_definition})"
                                    )
                                    values = ",".join(
                                        f"('{label}',0)" for label in visible
                                    )
                                    cursor.execute("INSERT INTO target VALUES" + values)
                                    cursor.execute("BEGIN")
                                    cursor.execute(
                                        f"INSERT INTO target VALUES('{pending}',0)"
                                    )
                                with waiter.cursor() as cursor:
                                    cursor.execute("USE probe")
                                    cursor.execute("BEGIN")
                                action = (
                                    "UPDATE target SET n=1"
                                    if verb == "UPDATE"
                                    else "DELETE FROM target"
                                )
                                statement = (
                                    f"{action} ORDER BY rank_value {direction} LIMIT 1"
                                )
                                started = threading.Event()
                                outcome = {}

                                def write(
                                    waiter=waiter,
                                    started=started,
                                    statement=statement,
                                    outcome=outcome,
                                ):
                                    try:
                                        with waiter.cursor() as cursor:
                                            started.set()
                                            cursor.execute(statement)
                                            outcome["affected"] = cursor.rowcount
                                            cursor.execute("COMMIT")
                                    except pymysql.MySQLError as error:
                                        outcome["error"] = error

                                thread = threading.Thread(target=write, daemon=True)
                                thread.start()
                                assert started.wait(2), statement
                                thread.join(0.2)
                                assert thread.is_alive(), (statement, "did not wait")
                                with owner.cursor() as cursor:
                                    cursor.execute("COMMIT")
                                thread.join(5)
                                assert not thread.is_alive(), statement
                                assert outcome == {"affected": 1}, (statement, outcome)
                                with owner.cursor() as cursor:
                                    cursor.execute(
                                        "SELECT rank_value,n FROM target ORDER BY rank_value"
                                    )
                                    rows = cursor.fetchall()
                                expected = tuple(
                                    (label, 1 if label == pending else 0)
                                    for label in ("z", "a", "m")
                                    if verb == "UPDATE" or label != pending
                                )
                                assert rows == expected, (statement, rows, expected)
                                print(
                                    key_definition,
                                    verb,
                                    direction,
                                    pending,
                                    "waited, affected 1",
                                    rows,
                                )
                            finally:
                                waiter.close()
                owner.close()
            finally:
                subprocess.run(
                    [
                        "mysqladmin",
                        "--no-defaults",
                        f"--socket={socket}",
                        "-uroot",
                        "shutdown",
                    ],
                    check=False,
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                )
                server.wait(timeout=20)


if __name__ == "__main__":
    main()
