"""Audit foreign-key rename collisions with one disposable MySQL per case."""

import json
import re
import runpy
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ORACLE = runpy.run_path(str(ROOT / "torture/scripts/fulltext-transaction-oracle.py"))
RESET = ['USE probe', 'CREATE DATABASE target', 'CREATE TABLE parent(n INT PRIMARY KEY)']
OBSERVE = [
    "SELECT TABLE_SCHEMA,TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA IN ('probe','target') ORDER BY TABLE_SCHEMA,TABLE_NAME",
    "SELECT CONSTRAINT_SCHEMA,CONSTRAINT_NAME,TABLE_NAME,REFERENCED_TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA IN ('probe','target') ORDER BY CONSTRAINT_SCHEMA,TABLE_NAME,CONSTRAINT_NAME",
]
CASES = [
    ("rename-generated-collision", [
        "CREATE TABLE child(a INT,FOREIGN KEY(a) REFERENCES parent(n))",
        "CREATE TABLE other(a INT,CONSTRAINT renamed_ibfk_1 FOREIGN KEY(a) REFERENCES parent(n))",
        "RENAME TABLE child TO renamed",
    ]),
    ("alter-generated-collision", [
        "CREATE TABLE child(a INT,FOREIGN KEY(a) REFERENCES parent(n))",
        "CREATE TABLE other(a INT,CONSTRAINT renamed_ibfk_1 FOREIGN KEY(a) REFERENCES parent(n))",
        "ALTER TABLE child RENAME TO renamed",
    ]),
    ("rename-cross-explicit", [
        "CREATE TABLE child(a INT,CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n))",
        "CREATE TABLE target.other(a INT,CONSTRAINT shared FOREIGN KEY(a) REFERENCES probe.parent(n))",
        "RENAME TABLE child TO target.renamed",
    ]),
    ("rename-cross-generated", [
        "CREATE TABLE child(a INT,FOREIGN KEY(a) REFERENCES parent(n))",
        "CREATE TABLE target.other(a INT,CONSTRAINT renamed_ibfk_1 FOREIGN KEY(a) REFERENCES probe.parent(n))",
        "RENAME TABLE child TO target.renamed",
    ]),
    ("rename-prefixed-explicit", [
        "CREATE TABLE child(a INT,CONSTRAINT child_ibfk_01 FOREIGN KEY(a) REFERENCES parent(n))",
        "RENAME TABLE child TO renamed",
    ]),
    ("rename-explicit", [
        "CREATE TABLE child(a INT,CONSTRAINT custom FOREIGN KEY(a) REFERENCES parent(n))",
        "RENAME TABLE child TO renamed",
    ]),
    ("rename-clear-collision", [
        "CREATE TABLE child(a INT,FOREIGN KEY(a) REFERENCES parent(n))",
        "CREATE TABLE other(a INT,CONSTRAINT renamed_ibfk_1 FOREIGN KEY(a) REFERENCES parent(n))",
        "RENAME TABLE other TO target.other,child TO renamed",
    ]),
    ("rename-batch-atomic", [
        "CREATE TABLE child(a INT,FOREIGN KEY(a) REFERENCES parent(n))",
        "CREATE TABLE other(a INT,CONSTRAINT renamed_ibfk_1 FOREIGN KEY(a) REFERENCES parent(n))",
        "RENAME TABLE parent TO parent_new,child TO renamed",
    ]),
    ("alter-generated", [
        "CREATE TABLE child(a INT,FOREIGN KEY(a) REFERENCES parent(n))",
        "ALTER TABLE child RENAME TO renamed",
    ]),
    ("alter-prefixed-explicit", [
        "CREATE TABLE child(a INT,CONSTRAINT child_ibfk_01 FOREIGN KEY(a) REFERENCES parent(n))",
        "ALTER TABLE child RENAME TO renamed",
    ]),
]


def run_case(name, body, output):
    statements = RESET + body + OBSERVE
    records = []

    def probe(client, _):
        socket = next(arg.split("=", 1)[1] for arg in client.process.args if arg.startswith("--socket="))
        log = Path(socket).parent / "server.log"
        try:
            result = subprocess.run(
                [*client.process.args, "--column-names", "--force"],
                input=";\n".join(statements) + ";\n",
                capture_output=True, text=True, timeout=30,
            )
            client.query("SELECT 1")
            record = dict(
                name=name, statements=statements, stdout=result.stdout,
                stderr=result.stderr, live=True,
                errors=[[int(code), state] for code, state in re.findall(r"ERROR (\d+) \((\w+)\)", result.stderr)],
            )
            records.append(record)
            (output / (name + ".json")).write_text(json.dumps(record, indent=2))
            print(name, result.stderr.strip(), flush=True)
        finally:
            (output / (name + ".log")).write_text(log.read_text())

    ORACLE["run"](probe)
    return records[0]


def main():
    output = Path(tempfile.mkdtemp(prefix="fsdb-fk-rename-evidence-"))
    results, failures = [], []
    for name, body in CASES:
        try:
            results.append(run_case(name, body, output))
        except Exception as error:
            failures.append(name)
            (output / (name + ".invalid")).write_text(repr(error))
            print(name, "INVALID ORACLE:", repr(error), flush=True)
    Path("/tmp/fsdb-fk-rename-native.json").write_text(json.dumps(results, indent=2))
    print("Evidence:", output)
    if failures:
        raise RuntimeError("Unusable native cases: " + ", ".join(failures))


if __name__ == "__main__":
    main()
