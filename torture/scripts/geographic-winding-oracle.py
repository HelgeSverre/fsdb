#!/usr/bin/env python3
"""Compare geographic polygon containment on fsdb and pinned MySQL 8.4.

Start both servers, then run, for example:
  python3 torture/scripts/geographic-winding-oracle.py \
    --mysql-port 33323 --mysql-database fsdb_test --fsdb-port 33324
"""

import argparse
import random
import subprocess


def statements(case_count: int, random_probes: int, vertex_probes: bool, antipodal_probes: bool) -> str:
    rng = random.Random(9173)
    queries = []
    for index in range(case_count):
        vertices = [(rng.uniform(-85, 85), rng.uniform(-180, 180)) for _ in range(4)]
        probes = [(rng.uniform(-85, 85), rng.uniform(-180, 180)) for _ in range(random_probes)]
        if vertex_probes:
            probes.extend(vertices)
        if antipodal_probes:
            probes.extend(((-lat, lon + 180 if lon <= 0 else lon - 180) for lat, lon in vertices))
        ring = vertices + vertices[:1]
        wkt = "POLYGON((" + ",".join(f"{lat:.8f} {lon:.8f}" for lat, lon in ring) + "))"
        polygon = f"ST_GeomFromText('{wkt}',4326)"
        columns = [str(index), f"ST_IsValid({polygon})"]
        columns.extend(
            f"ST_Within(ST_GeomFromText('POINT({lat:.8f} {lon:.8f})',4326),{polygon})"
            for lat, lon in probes
        )
        queries.append("SELECT " + ",".join(columns) + ";")
    return "\n".join(queries) + "\n"


def query(port: int, database: str | None, sql: str) -> list[list[str]]:
    command = [
        "mysql", "--protocol=tcp", "-h127.0.0.1", f"-P{port}", "-uroot",
        "--batch", "--raw", "--skip-column-names", "--default-character-set=utf8mb4",
    ]
    if database:
        command.append(database)
    result = subprocess.run(command, input=sql, text=True, capture_output=True, check=True)
    return [line.split("\t") for line in result.stdout.splitlines()]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mysql-port", type=int, required=True)
    parser.add_argument("--mysql-database", default="fsdb_test")
    parser.add_argument("--fsdb-port", type=int, required=True)
    parser.add_argument("--cases", type=int, default=300)
    parser.add_argument("--probes", type=int, default=12)
    parser.add_argument("--vertex-probes", action="store_true")
    parser.add_argument("--antipodal-probes", action="store_true")
    args = parser.parse_args()

    probe_count = args.probes + 4 * (args.vertex_probes + args.antipodal_probes)
    sql = statements(args.cases, args.probes, args.vertex_probes, args.antipodal_probes)
    mysql_rows = query(args.mysql_port, args.mysql_database, sql)
    fsdb_rows = query(args.fsdb_port, None, sql)
    if len(mysql_rows) != args.cases or len(fsdb_rows) != args.cases:
        raise RuntimeError(f"expected {args.cases} rows; got MySQL={len(mysql_rows)}, fsdb={len(fsdb_rows)}")

    valid = 0
    validity_differences = []
    containment_differences = []
    for mysql_row, fsdb_row in zip(mysql_rows, fsdb_rows):
        if len(mysql_row) != probe_count + 2 or len(fsdb_row) != probe_count + 2:
            raise RuntimeError(f"unexpected row width: MySQL={mysql_row}, fsdb={fsdb_row}")
        if mysql_row[0] != fsdb_row[0]:
            raise RuntimeError(f"case numbering differs: MySQL={mysql_row[0]}, fsdb={fsdb_row[0]}")
        case = mysql_row[0]
        if mysql_row[1] == "1":
            valid += 1
        if mysql_row[1] != fsdb_row[1]:
            validity_differences.append((case, mysql_row[1], fsdb_row[1]))
        for probe, (expected, actual) in enumerate(zip(mysql_row[2:], fsdb_row[2:])):
            if expected != actual:
                containment_differences.append((case, probe, expected, actual))

    print(f"rings={args.cases} mysql_valid={valid} point_queries={args.cases * probe_count}")
    print(f"containment_differences={len(containment_differences)} validity_differences={len(validity_differences)}")
    for difference in containment_differences[:12]:
        print("containment", difference)
    for difference in validity_differences[:12]:
        print("validity", difference)
    return 1 if containment_differences else 0


if __name__ == "__main__":
    raise SystemExit(main())
