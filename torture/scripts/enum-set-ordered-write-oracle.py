"""Check pending ENUM and SET index inserts in limited writes on MySQL 8.4.11.

Run with ``uv run --with pymysql python torture/scripts/enum-set-ordered-write-oracle.py``.
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
                for column_type, cases in (
                    (
                        "ENUM",
                        (
                            ("ASC", "z", ("a", "m"), ("z", "a", "m")),
                            ("DESC", "m", ("z", "a"), ("z", "a", "m")),
                        ),
                    ),
                    (
                        "SET",
                        (
                            ("ASC", "z", ("a", "m"), ("z", "a", "m")),
                            ("DESC", "m", ("z", "a"), ("z", "a", "m")),
                            ("DESC", "z,a", ("z", "a"), ("z", "a", "z,a")),
                        ),
                    ),
                ):
                    for composite in (False, True):
                        key_columns = "rank_value,id" if composite else "rank_value"
                        id_column = "id INT," if composite else ""

                        def row_value(label, row_id, composite=composite):
                            if composite:
                                return f"('{label}',{row_id},0)"
                            return f"('{label}',0)"

                        for key_definition in (
                            f"PRIMARY KEY({key_columns})",
                            f"KEY ix_rank({key_columns})",
                        ):
                            for verb in ("UPDATE", "DELETE"):
                                probes = cases
                                if composite:
                                    probes += (
                                        ("ASC", "a", ("a", "a"), ("a", "a", "a")),
                                    )
                                for direction, pending, visible, ordered in probes:
                                    tie = composite and visible == ("a", "a")
                                    waiter = connect(socket)
                                    try:
                                        with owner.cursor() as cursor:
                                            cursor.execute(
                                                "DROP TABLE IF EXISTS target"
                                            )
                                            cursor.execute(
                                                f"CREATE TABLE target(rank_value {column_type}('z','a','m'),{id_column}n INT,{key_definition})"
                                            )
                                            values = ",".join(
                                                row_value(label, index + 1)
                                                for index, label in enumerate(visible)
                                            )
                                            cursor.execute(
                                                "INSERT INTO target VALUES" + values
                                            )
                                            cursor.execute("BEGIN")
                                            cursor.execute(
                                                "INSERT INTO target VALUES"
                                                + row_value(pending, 0 if tie else 9)
                                            )
                                        with waiter.cursor() as cursor:
                                            cursor.execute("USE probe")
                                            cursor.execute("BEGIN")
                                        action = (
                                            "UPDATE target SET n=1"
                                            if verb == "UPDATE"
                                            else "DELETE FROM target"
                                        )
                                        order_by = f"rank_value {direction}"
                                        if composite:
                                            order_by += f",id {direction}"
                                        statement = (
                                            f"{action} ORDER BY {order_by} LIMIT 1"
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
                                                    outcome["affected"] = (
                                                        cursor.rowcount
                                                    )
                                                    cursor.execute("COMMIT")
                                            except pymysql.MySQLError as error:
                                                outcome["error"] = error

                                        thread = threading.Thread(
                                            target=write, daemon=True
                                        )
                                        thread.start()
                                        assert started.wait(2), statement
                                        thread.join(0.2)
                                        assert thread.is_alive(), (
                                            statement,
                                            "did not wait",
                                        )
                                        with owner.cursor() as cursor:
                                            cursor.execute("COMMIT")
                                        thread.join(5)
                                        assert not thread.is_alive(), statement
                                        assert outcome == {"affected": 1}, (
                                            statement,
                                            outcome,
                                        )
                                        with owner.cursor() as cursor:
                                            cursor.execute(
                                                "SELECT rank_value,id,n FROM target ORDER BY rank_value,id"
                                                if tie
                                                else "SELECT rank_value,n FROM target ORDER BY rank_value"
                                            )
                                            rows = cursor.fetchall()
                                        if tie:
                                            expected = (
                                                (("a", 0, 1), ("a", 1, 0), ("a", 2, 0))
                                                if verb == "UPDATE"
                                                else (("a", 1, 0), ("a", 2, 0))
                                            )
                                        else:
                                            expected = tuple(
                                                (label, 1 if label == pending else 0)
                                                for label in ordered
                                                if verb == "UPDATE" or label != pending
                                            )
                                        assert rows == expected, (
                                            statement,
                                            rows,
                                            expected,
                                        )
                                        print(
                                            column_type,
                                            key_definition,
                                            "composite" if composite else "single",
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
