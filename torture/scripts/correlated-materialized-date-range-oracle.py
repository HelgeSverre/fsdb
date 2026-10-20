"""Check correlated materialized DATE range results on native MySQL 8.4.11."""

import datetime
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
    with tempfile.TemporaryDirectory(prefix="fsdb-date-range-oracle-") as directory:
        root = pathlib.Path(directory)
        data = root / "data"
        data.mkdir()
        socket = str(root / "mysql.sock")
        server = connection = None
        with (root / "mysql.log").open("w") as log:
            try:
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
                connection = connect(socket)
                with connection.cursor() as cursor:
                    cursor.execute("SELECT VERSION()")
                    version = cursor.fetchone()[0]
                    assert version.startswith("8.4.11"), version
                    for query in [
                        "CREATE DATABASE probe",
                        "USE probe",
                        "CREATE TABLE outer_bounds(id INT PRIMARY KEY,min_d DATE,max_d DATE)",
                        "CREATE TABLE inner_rows(id INT PRIMARY KEY,d DATE,active INT)",
                        (
                            "INSERT INTO outer_bounds VALUES"
                            "(1,'2026-12-20','2026-12-22'),(2,NULL,NULL)"
                        ),
                    ]:
                        cursor.execute(query)
                    start = datetime.date(2026, 1, 1)
                    cursor.executemany(
                        "INSERT INTO inner_rows VALUES(%s,%s,1)",
                        [
                            (day + 1, start + datetime.timedelta(days=day))
                            for day in range(365)
                        ],
                    )
                    cases = [
                        ("d.d>=o.min_d", (12, 0)),
                        ("d.d>o.min_d", (11, 0)),
                        ("d.d BETWEEN o.min_d AND o.max_d", (3, 0)),
                        ("d.d BETWEEN o.min_d AND '2026-12-22'", (3, 0)),
                        ("d.d>=o.min_d OR d.id=1", (13, 1)),
                    ]
                    for predicate, expected in cases:
                        cursor.execute(
                            "WITH grouped AS (SELECT id,d,MAX(active) AS active "
                            "FROM inner_rows GROUP BY id,d) "
                            "SELECT o.id,(SELECT COUNT(*) FROM grouped d "
                            f"WHERE d.active=1 AND ({predicate})) "
                            "FROM outer_bounds o ORDER BY o.id"
                        )
                        actual = tuple(count for _, count in cursor.fetchall())
                        assert actual == expected, (predicate, actual, expected)
                    print(
                        f"MySQL {version}: {len(cases)} materialized DATE range cases passed"
                    )
                    cursor.execute("SET time_zone='+00:00'")
                    for temporal_type in ("DATETIME(6)", "TIMESTAMP(6)"):
                        cursor.execute("DROP TABLE inner_rows")
                        cursor.execute("DROP TABLE outer_bounds")
                        cursor.execute(
                            "CREATE TABLE outer_bounds(id INT PRIMARY KEY,"
                            f"min_d {temporal_type},max_d {temporal_type})"
                        )
                        cursor.execute(
                            "CREATE TABLE inner_rows(id INT PRIMARY KEY,"
                            f"d {temporal_type},active INT)"
                        )
                        cursor.execute(
                            "INSERT INTO outer_bounds VALUES"
                            "(1,'2026-12-20 12:34:56.123456',"
                            "'2026-12-22 12:34:56.123456'),(2,NULL,NULL)"
                        )
                        start_time = datetime.datetime(2026, 1, 1, 12, 34, 56, 123456)
                        cursor.executemany(
                            "INSERT INTO inner_rows VALUES(%s,%s,1)",
                            [
                                (day + 1, start_time + datetime.timedelta(days=day))
                                for day in range(365)
                            ],
                        )
                        temporal_cases = [
                            ("d.d>=o.min_d", (12, 0)),
                            ("d.d>o.min_d", (11, 0)),
                            ("d.d BETWEEN o.min_d AND o.max_d", (3, 0)),
                            (
                                "d.d BETWEEN o.min_d AND '2026-12-22 12:34:56.123456'",
                                (3, 0),
                            ),
                            ("d.d>='2026-12-30'", (2, 2)),
                            ("d.d>=o.min_d OR d.id=1", (13, 1)),
                        ]
                        for predicate, expected in temporal_cases:
                            cursor.execute(
                                "WITH grouped AS (SELECT id,d,MAX(active) AS active "
                                "FROM inner_rows GROUP BY id,d) "
                                "SELECT o.id,(SELECT COUNT(*) FROM grouped d "
                                f"WHERE d.active=1 AND ({predicate})) "
                                "FROM outer_bounds o ORDER BY o.id"
                            )
                            actual = tuple(count for _, count in cursor.fetchall())
                            assert actual == expected, (
                                temporal_type,
                                predicate,
                                actual,
                                expected,
                            )
                        print(
                            f"MySQL {version}: {len(temporal_cases)} materialized "
                            f"{temporal_type} range cases passed"
                        )
                    cursor.execute("DROP TABLE inner_rows")
                    cursor.execute("DROP TABLE outer_bounds")
                    cursor.execute(
                        "CREATE TABLE outer_bounds(id INT PRIMARY KEY,"
                        "min_d TIME(6),max_d TIME(6))"
                    )
                    cursor.execute(
                        "CREATE TABLE inner_rows(id INT PRIMARY KEY,d TIME(6),active INT)"
                    )
                    cursor.execute(
                        "INSERT INTO outer_bounds VALUES"
                        "(1,'350:12:34.123456','352:12:34.123456'),(2,NULL,NULL)"
                    )
                    cursor.executemany(
                        "INSERT INTO inner_rows VALUES(%s,%s,1)",
                        [(1, "-12:12:34.123456")]
                        + [(hour + 2, f"{hour}:12:34.123456") for hour in range(365)],
                    )
                    time_cases = [
                        ("d.d>=o.min_d", (15, 0)),
                        ("d.d>o.min_d", (14, 0)),
                        ("d.d BETWEEN o.min_d AND o.max_d", (3, 0)),
                        ("d.d BETWEEN o.min_d AND '352:12:34.123456'", (3, 0)),
                        ("d.d>=o.min_d OR d.id=1", (16, 1)),
                    ]
                    for predicate, expected in time_cases:
                        cursor.execute(
                            "WITH grouped AS (SELECT id,d,MAX(active) AS active "
                            "FROM inner_rows GROUP BY id,d) "
                            "SELECT o.id,(SELECT COUNT(*) FROM grouped d "
                            f"WHERE d.active=1 AND ({predicate})) "
                            "FROM outer_bounds o ORDER BY o.id"
                        )
                        actual = tuple(count for _, count in cursor.fetchall())
                        assert actual == expected, (predicate, actual, expected)
                    print(
                        f"MySQL {version}: {len(time_cases)} materialized "
                        "TIME(6) range cases passed"
                    )
            finally:
                if connection:
                    connection.close()
                if server and server.poll() is None:
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
