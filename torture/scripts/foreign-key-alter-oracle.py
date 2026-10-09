"""Probe combined foreign-key ALTER definitions on isolated native MySQL."""

import json
import re
import runpy
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ORACLE = runpy.run_path(str(ROOT / "torture/scripts/fulltext-transaction-oracle.py"))
RESET = ['USE probe', 'CREATE TABLE parent(n INT PRIMARY KEY,m INT)', 'CREATE TABLE child(a INT,b INT,CONSTRAINT fk FOREIGN KEY(a) REFERENCES parent(n))']
OBSERVE = [
    "SELECT TABLE_NAME,COLUMN_NAME,COLUMN_TYPE,IS_NULLABLE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA='probe' ORDER BY TABLE_NAME,ORDINAL_POSITION",
    "SELECT TABLE_NAME,CONSTRAINT_NAME,COLUMN_NAME,REFERENCED_TABLE_NAME,REFERENCED_COLUMN_NAME FROM information_schema.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA='probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME,ORDINAL_POSITION",
]
CASES = [
    ("drop-missing", [
        "ALTER TABLE child DROP FOREIGN KEY missing",
    ]),
    ("drop-twice", [
        "ALTER TABLE child DROP FOREIGN KEY fk,DROP FOREIGN KEY fk",
    ]),
    ("add-drop-new", [
        "ALTER TABLE child ADD CONSTRAINT new_fk FOREIGN KEY(b) REFERENCES parent(n),DROP FOREIGN KEY new_fk",
    ]),
    ("drop-add-same", [
        "ALTER TABLE child DROP FOREIGN KEY fk,ADD CONSTRAINT fk FOREIGN KEY(b) REFERENCES parent(n)",
    ]),
    ("drop-child-column", [
        "ALTER TABLE child DROP COLUMN a",
    ]),
    ("drop-parent-column", [
        "ALTER TABLE parent DROP COLUMN n",
    ]),
    ("drop-fk-column", [
        "ALTER TABLE child DROP FOREIGN KEY fk,DROP COLUMN a",
    ]),
    ("drop-column-fk", [
        "ALTER TABLE child DROP COLUMN a,DROP FOREIGN KEY fk",
    ]),
    ("add-fk-column", [
        "ALTER TABLE child ADD CONSTRAINT new_fk FOREIGN KEY(c) REFERENCES parent(n),ADD COLUMN c INT",
    ]),
    ("add-column-fk", [
        "ALTER TABLE child ADD COLUMN c INT,ADD CONSTRAINT new_fk FOREIGN KEY(c) REFERENCES parent(n)",
    ]),
    ("rename-child-column", [
        "ALTER TABLE child RENAME COLUMN a TO renamed",
    ]),
    ("rename-parent-column", [
        "ALTER TABLE parent RENAME COLUMN n TO renamed",
    ]),
    ("drop-child-off", [
        "SET foreign_key_checks=0",
        "ALTER TABLE child DROP COLUMN a",
    ]),
    ("drop-parent-off", [
        "SET foreign_key_checks=0",
        "ALTER TABLE parent DROP COLUMN n",
    ]),
    ("add-rename-table", [
        "ALTER TABLE child ADD FOREIGN KEY(b) REFERENCES parent(n),RENAME TO renamed",
    ]),
    ("rename-table-add", [
        "ALTER TABLE child RENAME TO renamed,ADD FOREIGN KEY(b) REFERENCES parent(n)",
    ]),
    ("explicit-add-rename-table", [
        "ALTER TABLE child ADD CONSTRAINT child_ibfk_9 FOREIGN KEY(b) REFERENCES parent(n),RENAME TO renamed",
    ]),
    ("add-old-rename-column", [
        "ALTER TABLE child ADD FOREIGN KEY(b) REFERENCES parent(n),RENAME COLUMN b TO c",
    ]),
    ("add-new-rename-column", [
        "ALTER TABLE child ADD FOREIGN KEY(c) REFERENCES parent(n),RENAME COLUMN b TO c",
    ]),
    ("drop-parent-cross-schema", [
        "ALTER TABLE child DROP FOREIGN KEY fk",
        "CREATE DATABASE other",
        "CREATE TABLE other.external_child(a INT,CONSTRAINT external_fk FOREIGN KEY(a) REFERENCES probe.parent(n))",
        "ALTER TABLE parent DROP COLUMN n",
    ]),
]


def main():
    results = []
    for name, body in CASES:
        def probe(client, _, name=name, body=body):
            statements = RESET + body + OBSERVE
            result = subprocess.run(
                [*client.process.args, "--column-names", "--force"],
                input=";\n".join(statements) + ";\n",
                capture_output=True, text=True, timeout=30,
            )
            client.query("SELECT 1")
            results.append(dict(
                name=name, statements=statements, stdout=result.stdout,
                stderr=result.stderr,
                errors=[[int(code), state] for code, state in re.findall(r"ERROR (\d+) \((\w+)\)", result.stderr)],
            ))
            print(name, result.stderr.strip(), flush=True)
        ORACLE["run"](probe)
    Path("/tmp/fsdb-fk-alter-native.json").write_text(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
