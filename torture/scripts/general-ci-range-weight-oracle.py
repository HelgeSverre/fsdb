"""Inspect native MySQL 8.4.11 collation weights for a Unicode range."""

import argparse
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
    parser = argparse.ArgumentParser()
    parser.add_argument("start", type=lambda value: int(value, 16))
    parser.add_argument("stop", type=lambda value: int(value, 16))
    parser.add_argument("--collation", choices=("utf8mb4_general_ci", "utf8mb4_unicode_ci", "utf8mb4_unicode_520_ci"),
                        default="utf8mb4_general_ci")
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix="fsdb-general-weight-") as directory:
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
                    weights = {}
                    for codepoint in range(args.start, args.stop):
                        encoded = chr(codepoint).encode("utf-8").hex()
                        cursor.execute(
                            "SELECT HEX(WEIGHT_STRING(_utf8mb4 0x"
                            + encoded + " COLLATE " + args.collation + "))"
                        )
                        weights[codepoint] = cursor.fetchone()[0]
                    nonliteral = [
                        (f"U+{point:04X}", weight)
                        for point, weight in weights.items()
                        if weight != f"{point:04X}"
                    ]
                    groups = {}
                    for point, weight in weights.items():
                        groups.setdefault(weight, []).append(point)
                    duplicate_groups = [
                        [f"U+{point:04X}" for point in points]
                        for points in groups.values() if len(points) > 1
                    ]
                    print(f"MySQL {version}: {len(weights)} weights, "
                          f"{len(nonliteral)} nonliteral, {len(duplicate_groups)} duplicate groups")
                    print("nonliteral", nonliteral)
                    print("duplicate groups", duplicate_groups)
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
