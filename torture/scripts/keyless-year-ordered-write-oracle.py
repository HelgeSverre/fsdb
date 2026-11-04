"""Check pending YEAR index inserts in limited writes on MySQL 8.4.11.

Run with ``uv run --with pymysql python torture/scripts/keyless-year-ordered-write-oracle.py``.
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
    with tempfile.TemporaryDirectory(prefix="fsdb-year-write-") as directory:
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
                for verb in ("UPDATE", "DELETE"):
                    for direction, pending in (("ASC", 2020), ("DESC", 2024)):
                        waiter = connect(socket)
                        try:
                            with owner.cursor() as cursor:
                                cursor.execute("DROP TABLE IF EXISTS target")
                                cursor.execute(
                                    "CREATE TABLE target(rank_year YEAR,n INT,KEY ix_year(rank_year))"
                                )
                                cursor.execute(
                                    "INSERT INTO target VALUES(2021,0),(2022,0),(2023,0)"
                                )
                                cursor.execute("BEGIN")
                                cursor.execute(
                                    f"INSERT INTO target VALUES({pending},0)"
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
                                f"{action} ORDER BY rank_year {direction} LIMIT 1"
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
                                    "SELECT rank_year,n FROM target ORDER BY rank_year"
                                )
                                rows = cursor.fetchall()
                            expected = tuple(
                                (year, 1 if year == pending else 0)
                                for year in sorted((2021, 2022, 2023, pending))
                                if verb == "UPDATE" or year != pending
                            )
                            assert rows == expected, (statement, rows, expected)
                            print(verb, direction, pending, "waited, affected 1", rows)
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
