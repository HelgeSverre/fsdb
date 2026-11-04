"""Inspect native MySQL 8.4.11 collation weights for a Unicode range."""

import argparse
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
    raise RuntimeError("MySQL did not become ready")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("start", type=lambda value: int(value, 16))
    parser.add_argument("stop", type=lambda value: int(value, 16))
    parser.add_argument(
        "--collation",
        choices=(
            "utf8mb4_general_ci",
            "utf8mb4_unicode_ci",
            "utf8mb4_unicode_520_ci",
            "utf8mb3_general_ci",
            "utf8mb3_unicode_ci",
        ),
        default="utf8mb4_general_ci",
    )
    parser.add_argument(
        "--compare-fsdb",
        action="store_true",
        help="compare exact WEIGHT_STRING bytes with Release fsdb",
    )
    parser.add_argument(
        "--show-weights",
        action="store_true",
        help="print every MySQL weight instead of only mismatch summaries",
    )
    pairing = parser.add_mutually_exclusive_group()
    pairing.add_argument(
        "--pair-next",
        action="store_true",
        help="probe each character followed by the next code point",
    )
    pairing.add_argument(
        "--pair-with",
        type=lambda value: int(value, 16),
        help="probe each character followed by this hexadecimal code point",
    )
    args = parser.parse_args()
    if args.start < 0 or args.stop > 0x110000 or args.start >= args.stop:
        parser.error("expected a nonempty Unicode code-point range")
    if args.pair_next and args.stop > 0x10FFFF:
        parser.error("--pair-next requires a successor for every code point")
    if args.pair_with is not None and not 0 <= args.pair_with <= 0x10FFFF:
        parser.error("--pair-with requires a Unicode code point")
    with tempfile.TemporaryDirectory(prefix="fsdb-general-weight-") as directory:
        root = pathlib.Path(directory)
        data = root / "data"
        data.mkdir()
        mysql_socket = str(root / "mysql.sock")
        server = connection = fsdb_server = fsdb_connection = None
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
                        f"--socket={mysql_socket}",
                        f"--pid-file={root / 'mysql.pid'}",
                        "--mysqlx=0",
                        "--innodb-buffer-pool-size=64M",
                        "--innodb-redo-log-capacity=64M",
                    ],
                    stdout=log,
                    stderr=subprocess.STDOUT,
                )
                connection = connect(unix_socket=mysql_socket)
                if args.compare_fsdb:
                    if not FSDB.is_file():
                        raise RuntimeError(f"Build the Release fsdb CLI first: {FSDB}")
                    with socket.socket() as reservation:
                        reservation.bind(("127.0.0.1", 0))
                        port = reservation.getsockname()[1]
                    fsdb_server = subprocess.Popen(
                        [
                            "dotnet",
                            str(FSDB),
                            "--data-dir",
                            str(root / "fsdb-data"),
                            "--port",
                            str(port),
                        ],
                        stdout=log,
                        stderr=subprocess.STDOUT,
                    )
                    fsdb_connection = connect(host="127.0.0.1", port=port)
                with connection.cursor() as cursor:
                    cursor.execute("SELECT VERSION()")
                    version = cursor.fetchone()[0]
                    assert version.startswith("8.4.11"), version
                    weights = {}
                    mismatches = []
                    for codepoint in range(args.start, args.stop):
                        sample = chr(codepoint)
                        if args.pair_next:
                            sample += chr(codepoint + 1)
                        elif args.pair_with is not None:
                            sample += chr(args.pair_with)
                        encoded = sample.encode("utf-8").hex()
                        charset = args.collation.split("_", 1)[0]
                        query = (
                            "SELECT HEX(WEIGHT_STRING(_"
                            + charset
                            + " 0x"
                            + encoded
                            + " COLLATE "
                            + args.collation
                            + "))"
                        )
                        cursor.execute(query)
                        weights[codepoint] = cursor.fetchone()[0]
                        if fsdb_connection is not None:
                            with fsdb_connection.cursor() as fsdb_cursor:
                                fsdb_cursor.execute(query)
                                actual = fsdb_cursor.fetchone()[0]
                            if actual != weights[codepoint]:
                                mismatches.append(
                                    (f"U+{codepoint:04X}", weights[codepoint], actual)
                                )
                    nonliteral = (
                        [
                            (f"U+{point:04X}", weight)
                            for point, weight in weights.items()
                            if weight != f"{point:04X}"
                        ]
                        if not args.pair_next and args.pair_with is None
                        else []
                    )
                    groups = {}
                    for point, weight in weights.items():
                        groups.setdefault(weight, []).append(point)
                    duplicate_groups = [
                        [f"U+{point:04X}" for point in points]
                        for points in groups.values()
                        if len(points) > 1
                    ]
                    description = f"MySQL {version}: {len(weights)} weights"
                    if not args.pair_next and args.pair_with is None:
                        description += f", {len(nonliteral)} nonliteral"
                    description += f", {len(duplicate_groups)} duplicate groups"
                    print(description)
                    if args.show_weights:
                        print(
                            "weights",
                            [
                                (f"U+{point:04X}", weight)
                                for point, weight in weights.items()
                            ],
                        )
                    if not args.pair_next and args.pair_with is None:
                        print("nonliteral", nonliteral)
                    print("duplicate groups", duplicate_groups)
                    if fsdb_connection is not None:
                        print(f"fsdb exact-weight mismatches: {len(mismatches)}")
                        print("first mismatches", mismatches[:20])
            finally:
                if fsdb_connection:
                    fsdb_connection.close()
                if fsdb_server and fsdb_server.poll() is None:
                    fsdb_server.terminate()
                    fsdb_server.wait(timeout=20)
                if connection:
                    connection.close()
                if server and server.poll() is None:
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
                    server.wait(timeout=20)


if __name__ == "__main__":
    main()
