"""Compare geographic polygon validity with disposable MySQL 8.4 and fsdb servers.

Build the Release CLI first, then run with ``uv run --with pymysql``.
The known pre-fix mismatch set is {76, 92}; any change to it is reported.
"""

import argparse
import pathlib
import random
import socket
import subprocess
import tempfile
import time
from itertools import pairwise

import pymysql

REPO = pathlib.Path(__file__).resolve().parents[2]
FSDB = REPO / "src/Fsdb.Cli/bin/Release/net10.0/Fsdb.Cli.dll"
KNOWN_MISMATCHES = {76: (0, 1), 92: (1, 0)}
KNOWN_RELATION_MISMATCHES = {
    76: {(0, 2): ((1, 1, 0), (0, 0, 0))},
    92: {(1, 2): ((1, 0, 1), (1, 1, 0))},
}


def connect(**options):
    for _ in range(100):
        try:
            return pymysql.connect(user="root", autocommit=True, **options)
        except pymysql.MySQLError:
            time.sleep(0.1)
    raise RuntimeError("database server did not become ready")


def scalar(connection, sql):
    with connection.cursor() as cursor:
        cursor.execute(sql)
        return cursor.fetchone()[0]


def polygon_wkts(count, seed):
    rng = random.Random(seed)
    for _ in range(count):
        vertices = [(rng.uniform(-85, 85), rng.uniform(-180, 180)) for _ in range(4)]
        # Preserve the point-probe draws in geographic-winding-oracle.py so
        # case numbers keep referring to the same 300-ring corpus.
        for _ in range(12):
            rng.uniform(-85, 85)
            rng.uniform(-180, 180)
        ring = vertices + vertices[:1]
        yield "POLYGON((" + ",".join(f"{lat:.8f} {lon:.8f}" for lat, lon in ring) + "))"


def segment_relations(connection, wkt):
    points = wkt.removeprefix("POLYGON((").removesuffix("))").split(",")
    segments = [f"LINESTRING({start},{end})" for start, end in pairwise(points)]
    results = []
    for first in range(len(segments)):
        for second in range(first + 1, len(segments)):
            left = f"ST_GeomFromText('{segments[first]}',4326)"
            right = f"ST_GeomFromText('{segments[second]}',4326)"
            with connection.cursor() as cursor:
                cursor.execute(
                    "SELECT ST_Intersects(" + left + "," + right + "),"
                    "ST_Crosses(" + left + "," + right + "),"
                    "ST_Touches(" + left + "," + right + ")"
                )
                results.append((first, second, cursor.fetchone()))
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--count", type=int, default=300)
    parser.add_argument("--seed", type=int, default=9173)
    parser.add_argument("--diagnose-segments", action="store_true")
    args = parser.parse_args()
    if args.count < 1:
        parser.error("--count must be positive")
    if not FSDB.is_file():
        raise RuntimeError(f"build the Release CLI first: {FSDB}")
    with tempfile.TemporaryDirectory(prefix="fsdb-geographic-validity-") as directory:
        root = pathlib.Path(directory)
        data = root / "mysql"
        data.mkdir()
        mysql_socket = str(root / "mysql.sock")
        mysql = fsdb = mysql_connection = fsdb_connection = None
        with (
            (root / "mysql.log").open("w") as mysql_log,
            (root / "fsdb.log").open("w") as fsdb_log,
        ):
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
                    stdout=mysql_log,
                    stderr=subprocess.STDOUT,
                )
                mysql = subprocess.Popen(
                    [
                        "mysqld",
                        "--no-defaults",
                        f"--datadir={data}",
                        "--skip-networking",
                        f"--socket={mysql_socket}",
                        f"--pid-file={root / 'mysql.pid'}",
                        "--mysqlx=0",
                        "--innodb-buffer-pool-size=64M",
                        "--innodb-redo-log-capacity=64M",
                    ],
                    stdout=mysql_log,
                    stderr=subprocess.STDOUT,
                )
                mysql_connection = connect(unix_socket=mysql_socket)
                version = scalar(mysql_connection, "SELECT VERSION()")
                assert version.startswith("8.4.11"), version

                with socket.socket() as reservation:
                    reservation.bind(("127.0.0.1", 0))
                    port = reservation.getsockname()[1]
                fsdb = subprocess.Popen(
                    ["dotnet", str(FSDB), "--port", str(port)],
                    stdout=fsdb_log,
                    stderr=subprocess.STDOUT,
                )
                fsdb_connection = connect(host="127.0.0.1", port=port)

                mismatches = {}
                relation_mismatches = {}
                mysql_valid = 0
                for case, wkt in enumerate(polygon_wkts(args.count, args.seed)):
                    sql = f"SELECT ST_IsValid(ST_GeomFromText('{wkt}',4326))"
                    expected = scalar(mysql_connection, sql)
                    actual = scalar(fsdb_connection, sql)
                    mysql_valid += expected
                    if expected != actual:
                        mismatches[case] = expected, actual
                        print(
                            f"case {case}: MySQL={expected} fsdb={actual} {wkt}",
                            flush=True,
                        )
                        if args.diagnose_segments:
                            mysql_relations = segment_relations(mysql_connection, wkt)
                            fsdb_relations = segment_relations(fsdb_connection, wkt)
                            differences = {}
                            for mysql_relation, fsdb_relation in zip(
                                mysql_relations, fsdb_relations
                            ):
                                first, second, expected_relation = mysql_relation
                                _, _, actual_relation = fsdb_relation
                                if expected_relation != actual_relation:
                                    differences[first, second] = (
                                        expected_relation,
                                        actual_relation,
                                    )
                                print(
                                    f"  edges {first},{second}: MySQL={expected_relation} fsdb={actual_relation}",
                                    flush=True,
                                )
                            relation_mismatches[case] = differences

                print(
                    f"MySQL {version}: seed={args.seed}, {args.count} rings, {mysql_valid} valid, mismatches={mismatches}"
                )
                if args.count == 300 and args.seed == 9173:
                    assert mismatches == KNOWN_MISMATCHES, (
                        "geographic validity baseline changed; investigate before updating it",
                        mismatches,
                    )
                    if args.diagnose_segments:
                        assert relation_mismatches == KNOWN_RELATION_MISMATCHES, (
                            "geographic segment relations changed; investigate before updating them",
                            relation_mismatches,
                        )
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
                        [
                            "mysqladmin",
                            "--no-defaults",
                            f"--socket={mysql_socket}",
                            "-uroot",
                            "shutdown",
                        ],
                        check=False,
                        stdout=subprocess.DEVNULL,
                        stderr=subprocess.DEVNULL,
                    )
                    mysql.wait(timeout=20)


if __name__ == "__main__":
    main()
