"""Check shared geographic endpoints and global rings against pinned MySQL."""

import importlib.util
import json
import pathlib
import random
import socket
import subprocess
import sys
import tempfile

REPO = pathlib.Path(__file__).resolve().parents[3]
sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location(
    "validity_oracle", REPO / "torture/scripts/geographic-validity-oracle.py"
)
VALIDITY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDITY)


def relation(connection, first, shared, second):
    left = f"ST_GeomFromText('LINESTRING({first},{shared})',4326)"
    right = f"ST_GeomFromText('LINESTRING({shared},{second})',4326)"
    with connection.cursor() as cursor:
        cursor.execute(
            f"SELECT ST_Intersects({left},{right}),ST_Crosses({left},{right}),"
            f"ST_Touches({left},{right}),ST_Distance({left},{right})"
        )
        return cursor.fetchone()


def main():
    address = subprocess.check_output(
        ["docker", "compose", "-f", "torture/compose.yaml", "port", "mysql", "3306"],
        cwd=REPO,
        text=True,
    ).strip()
    mysql = VALIDITY.connect(
        host="127.0.0.1", port=int(address.rsplit(":", 1)[1]), password="torture-secret"
    )
    version = VALIDITY.scalar(mysql, "SELECT VERSION()")
    assert version.startswith("8.4.11"), version
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = reservation.getsockname()[1]
    with (
        tempfile.TemporaryDirectory(prefix="fsdb-endpoint-oracle-") as directory,
        pathlib.Path(directory, "fsdb.log").open("w") as log,
    ):
        server = subprocess.Popen(
            ["dotnet", str(VALIDITY.FSDB), "--port", str(port)],
            stdout=log,
            stderr=subprocess.STDOUT,
            cwd=REPO,
        )
        fsdb = None
        try:
            fsdb = VALIDITY.connect(host="127.0.0.1", port=port)
            rng = random.Random(33092)
            endpoint_differences = []
            for case in range(1000):
                points = [
                    f"{rng.uniform(-85, 85):.8f} {rng.uniform(-180, 180):.8f}"
                    for _ in range(3)
                ]
                expected = relation(mysql, *points)
                actual = relation(fsdb, *points)
                if expected != actual:
                    endpoint_differences.append((case, points, expected, actual))
            rings = {}
            for seed in [9173, 9174, 9175]:
                differences = {}
                for case, text in enumerate(VALIDITY.polygon_wkts(1000, seed)):
                    sql = f"SELECT ST_IsValid(ST_GeomFromText('{text}',4326))"
                    expected = VALIDITY.scalar(mysql, sql)
                    actual = VALIDITY.scalar(fsdb, sql)
                    if expected != actual:
                        differences[case] = [expected, actual]
                rings[seed] = differences
            summary = {
                "mysql_version": version,
                "endpoint_seed": 33092,
                "endpoint_cases": 1000,
                "endpoint_differences": endpoint_differences,
                "rings_per_seed": 1000,
                "ring_differences": rings,
            }
            print(json.dumps(summary, indent=2), flush=True)
            assert not endpoint_differences, "new endpoint relation disagreement"
            assert rings == {9173: {76: [0, 1]}, 9174: {}, 9175: {}}, (
                "global ring mismatch set changed; review against the oracle"
            )
        finally:
            if fsdb is not None:
                fsdb.close()
            mysql.close()
            server.terminate()
            try:
                server.wait(timeout=5)
            except subprocess.TimeoutExpired:
                server.kill()
                server.wait()


if __name__ == "__main__":
    main()
