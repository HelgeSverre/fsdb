"""Compare large-radius ST_DISTANCE_SPHERE results with native MySQL 8.4.11.

Build Fsdb.Cli in Release first. Requires native mysqld/mysqladmin and PyMySQL.
Both servers and their data directories are disposable.
"""

import math
import pathlib
import socket
import subprocess
import tempfile
import time

import pymysql


REPO = pathlib.Path(__file__).resolve().parents[2]
FSDB = REPO / "src/Fsdb.Cli/bin/Release/net10.0/Fsdb.Cli.dll"


def connect(**options):
    for _ in range(100):
        try:
            return pymysql.connect(user="root", autocommit=True, **options)
        except pymysql.MySQLError:
            time.sleep(0.1)
    raise RuntimeError("database server did not become ready")


def run(connection, first, second, radius):
    sql = (
        "SELECT ST_DISTANCE_SPHERE("
        f"ST_GeomFromText('{first}',4326),"
        f"ST_GeomFromText('{second}',4326),{radius})"
    )
    try:
        with connection.cursor() as cursor:
            cursor.execute(sql)
            return cursor.fetchone()[0]
    except pymysql.MySQLError as error:
        return "error", error.args


def main():
    if not FSDB.exists():
        raise RuntimeError(f"Build the Release CLI first: {FSDB}")

    with tempfile.TemporaryDirectory(prefix="fsdb-sphere-radius-") as directory:
        root = pathlib.Path(directory)
        data = root / "mysql-data"
        mysql_socket = str(root / "mysql.sock")
        mysql = fsdb = mysql_connection = fsdb_connection = None
        with (root / "mysql.log").open("w") as mysql_log, (root / "fsdb.log").open("w") as fsdb_log:
            try:
                subprocess.run(
                    ["mysqld", "--no-defaults", "--initialize-insecure", f"--datadir={data}",
                     "--innodb-redo-log-capacity=64M"],
                    check=True, stdout=mysql_log, stderr=subprocess.STDOUT,
                )
                mysql = subprocess.Popen(
                    ["mysqld", "--no-defaults", f"--datadir={data}", "--skip-networking",
                     f"--socket={mysql_socket}", f"--pid-file={root / 'mysql.pid'}",
                     "--mysqlx=0", "--innodb-buffer-pool-size=64M",
                     "--innodb-redo-log-capacity=64M"],
                    stdout=mysql_log, stderr=subprocess.STDOUT,
                )
                mysql_connection = connect(unix_socket=mysql_socket)
                with mysql_connection.cursor() as cursor:
                    cursor.execute("SELECT VERSION()")
                    version = cursor.fetchone()[0]
                assert version.startswith("8.4.11"), version

                with socket.socket() as reservation:
                    reservation.bind(("127.0.0.1", 0))
                    port = reservation.getsockname()[1]
                fsdb = subprocess.Popen(
                    ["dotnet", str(FSDB), "--port", str(port)],
                    stdout=fsdb_log, stderr=subprocess.STDOUT,
                )
                fsdb_connection = connect(host="127.0.0.1", port=port)

                cases = [
                    ("POINT(0 0)", "POINT(0 0)", "1e308"),
                    ("POINT(0 0)", "POINT(0 1)", "1e308"),
                    ("POINT(0 0)", "POINT(0 180)", "1e307"),
                    ("POINT(0 0)", "POINT(0 180)", "1e308"),
                    ("MULTIPOINT((0 0),(0 180))", "POINT(0 0)", "1e308"),
                    ("MULTIPOINT((0 180),(0 1))", "POINT(0 0)", "1e308"),
                ]
                for first, second, radius in cases:
                    expected = run(mysql_connection, first, second, radius)
                    actual = run(fsdb_connection, first, second, radius)
                    if isinstance(expected, tuple):
                        assert actual == expected, (first, second, radius, expected, actual)
                    else:
                        assert math.isclose(actual, expected, rel_tol=1e-13, abs_tol=0), (
                            first, second, radius, expected, actual
                        )
                print(f"MySQL {version}: {len(cases)} large-radius cases matched")
            finally:
                if fsdb_connection:
                    fsdb_connection.close()
                if mysql_connection:
                    mysql_connection.close()
                if fsdb and fsdb.poll() is None:
                    fsdb.terminate()
                    fsdb.wait(timeout=20)
                if mysql and mysql.poll() is None:
                    subprocess.run(
                        ["mysqladmin", "--no-defaults", f"--socket={mysql_socket}", "-uroot", "shutdown"],
                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    )
                    mysql.wait(timeout=20)


if __name__ == "__main__":
    main()
