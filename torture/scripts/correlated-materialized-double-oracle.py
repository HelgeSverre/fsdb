"""Check materialized correlated DOUBLE expression results against MySQL 8.4."""

import pathlib
import subprocess
import tempfile
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
    with tempfile.TemporaryDirectory(prefix="fsdb-double-oracle-") as directory:
        root = pathlib.Path(directory)
        data = root / "data"
        data.mkdir()
        socket = str(root / "mysql.sock")
        server = connection = None

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
                connection = connect(socket)
                with connection.cursor() as cursor:
                    cursor.execute("SELECT VERSION()")
                    version = cursor.fetchone()[0]
                    assert version.startswith("8.4.11"), version
                    for query in [
                        "CREATE DATABASE probe", "USE probe",
                        "CREATE TABLE outer_bounds(id INT PRIMARY KEY,x DOUBLE,d DECIMAL(22,2),i BIGINT,s VARCHAR(32))",
                        "CREATE TABLE inner_rows(id INT PRIMARY KEY,k DOUBLE)",
                        "INSERT INTO outer_bounds VALUES"
                        "(1,12.5,12.50,12,'12.5'),(2,999.0,999.00,999,'999'),"
                        "(3,NULL,NULL,NULL,NULL),(4,-12.5,-12.50,-12,'-12.5'),"
                        "(5,-0.0,0.00,0,'0'),"
                        "(6,9007199254740993,9007199254740993.00,9007199254740993,'9007199254740993')",
                    ]:
                        cursor.execute(query)
                    cursor.executemany(
                        "INSERT INTO inner_rows VALUES(%s,%s)",
                        [(i, f"{i - 500}.5") for i in range(1000)]
                        + [(1000, None), (1001, 0.0),
                           (1002, 9007199254740992), (1003, 9007199254740994)],
                    )

                    cases = [
                        ("d.k=o.x", (1, 0, 0, 1, 1, 1)),
                        ("d.k+0e0=o.x", (1, 0, 0, 1, 1, 1)),
                        ("o.x=d.k+0e0", (1, 0, 0, 1, 1, 1)),
                        ("d.k*1e0=o.x", (1, 0, 0, 1, 1, 1)),
                        ("d.k+0e0=o.d", (1, 0, 0, 1, 1, 1)),
                        ("d.k+0e0=o.i", (0, 0, 0, 0, 1, 1)),
                        ("d.k+0e0=o.s", (1, 0, 0, 1, 1, 1)),
                        ("d.k+0e0=o.x OR d.id=12", (2, 1, 1, 2, 2, 2)),
                    ]
                    for predicate, expected in cases:
                        query = (
                            "SELECT o.id,(SELECT COUNT(*) FROM "
                            "(SELECT DISTINCT id,k FROM inner_rows) d "
                            f"WHERE {predicate}) FROM outer_bounds o ORDER BY o.id"
                        )
                        cursor.execute(query)
                        actual = tuple(count for _, count in cursor.fetchall())
                        assert actual == expected, (predicate, actual, expected)
                        if "o.s" in predicate:
                            assert cursor._result.warning_count == 0, predicate
                    in_cases = [
                        ("d.k+0e0 IN (o.s,o.x)", (1, 0, 0, 1, 1, 1)),
                        ("d.k+0e0 IN (o.s,o.d)", (1, 0, 0, 1, 1, 1)),
                        ("d.k+0e0 IN (o.s,CAST(NULL AS DOUBLE))", (1, 0, 0, 1, 1, 1)),
                        ("d.k+0e0 IN (o.s,o.s)", (1, 0, 0, 1, 1, 1)),
                        ("d.k+0e0 IN (o.s,o.x) OR d.id=12", (2, 1, 1, 2, 2, 2)),
                    ]
                    for predicate, expected in in_cases:
                        cursor.execute(
                            "SELECT o.id,(SELECT COUNT(*) FROM "
                            "(SELECT DISTINCT id,k FROM inner_rows) d "
                            f"WHERE {predicate}) FROM outer_bounds o ORDER BY o.id"
                        )
                        actual = tuple(count for _, count in cursor.fetchall())
                        assert actual == expected, (predicate, actual, expected)
                        assert cursor._result.warning_count == 0, predicate
                    range_cases = [
                        ("d.k+0e0<o.x", (513, 1001, 0, 488, 500, 1001)),
                        ("o.x>d.k+0e0", (513, 1001, 0, 488, 500, 1001)),
                        ("d.k+0e0<=o.x", (514, 1001, 0, 489, 501, 1002)),
                        ("d.k+0e0>o.d", (489, 2, 0, 514, 502, 1)),
                        ("d.k+0e0>=o.i", (490, 2, 0, 514, 503, 2)),
                        ("d.k+0e0 BETWEEN o.x-1e0 AND o.x+1e0", (3, 0, 0, 3, 2, 1)),
                        ("d.k+0e0<o.s", (513, 1001, 0, 488, 500, 1001)),
                        ("d.k+0e0 BETWEEN o.s AND o.s", (1, 0, 0, 1, 1, 1)),
                        ("d.k+0e0<o.x OR d.id=999", (514, 1001, 1, 489, 501, 1001)),
                    ]
                    for predicate, expected in range_cases:
                        cursor.execute(
                            "SELECT o.id,(SELECT COUNT(*) FROM "
                            "(SELECT DISTINCT id,k FROM inner_rows) d "
                            f"WHERE {predicate}) FROM outer_bounds o ORDER BY o.id"
                        )
                        actual = tuple(count for _, count in cursor.fetchall())
                        assert actual == expected, (predicate, actual, expected)
                        if "o.s" in predicate:
                            assert cursor._result.warning_count == 0, predicate
                    cursor.execute("INSERT INTO outer_bounds VALUES(7,NULL,NULL,NULL,'12.5x')")
                    for predicate in ("d.k+0e0=o.s", "d.k+0e0 BETWEEN o.s AND o.s", "d.k+0e0 IN (o.s,o.x)"):
                        cursor.execute(
                            "SELECT (SELECT COUNT(*) FROM "
                            "(SELECT DISTINCT id,k FROM inner_rows) d "
                            f"WHERE {predicate}) FROM outer_bounds o WHERE o.id=7"
                        )
                        assert cursor.fetchone()[0] == 1, predicate
                        assert cursor._result.warning_count > 0, predicate
                    for text, expected, has_warning in (
                        ("1e-500", 1, False), ("1e309", 0, True), (" 12.5 ", 1, False)
                    ):
                        cursor.execute("UPDATE outer_bounds SET s=%s WHERE id=7", (text,))
                        for predicate in ("d.k+0e0=o.s", "d.k+0e0 IN (o.s,o.x)"):
                            cursor.execute(
                                "SELECT (SELECT COUNT(*) FROM "
                                "(SELECT DISTINCT id,k FROM inner_rows) d "
                                f"WHERE {predicate}) FROM outer_bounds o WHERE o.id=7"
                            )
                            assert cursor.fetchone()[0] == expected, (text, predicate)
                            assert (cursor._result.warning_count > 0) == has_warning, (text, predicate)
                    cursor.execute(
                        "SELECT x=i,x+0e0=i,9007199254740992e0=i,"
                        "9007199254740992e0+0e0=i "
                        "FROM outer_bounds WHERE id=6"
                    )
                    assert cursor.fetchone() == (1, 1, 0, 0)
                    print(f"MySQL {version}: {len(cases)} DOUBLE equalities, {len(in_cases)} IN forms, and {len(range_cases)} ranges match")
            finally:
                if connection:
                    connection.close()
                if server and server.poll() is None:
                    subprocess.run(
                        ["mysqladmin", "--no-defaults", f"--socket={socket}", "-uroot", "shutdown"],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    )
                    server.wait(timeout=20)


if __name__ == "__main__":
    main()
