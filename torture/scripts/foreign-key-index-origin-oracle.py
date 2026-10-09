"""Probe foreign-key index provenance on disposable native MySQL 8.4."""

import json
import re
import runpy
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ORACLE = runpy.run_path(str(ROOT / "torture/scripts/fulltext-transaction-oracle.py"))
RESET = [
    "USE probe",
    "SET foreign_key_checks=0",
    "DROP TABLE IF EXISTS child,parent",
    "SET foreign_key_checks=1",
    "CREATE TABLE parent(n INT PRIMARY KEY)",
    "CREATE TABLE child(a INT,b INT,CONSTRAINT fk FOREIGN KEY(a) REFERENCES parent(n))",
]
OBSERVE = [
    "SHOW INDEX FROM child",
    "SELECT CONSTRAINT_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='probe' ORDER BY CONSTRAINT_NAME",
]
CASES = [
    ("duplicate-primary", ["ALTER TABLE child ADD PRIMARY KEY(a)", "ALTER TABLE child ADD PRIMARY KEY(b)"]),
    ("drop-child", [
        "ALTER TABLE child DROP INDEX fk",
    ]),
    ("drop-child-off", [
        "SET foreign_key_checks=0",
        "ALTER TABLE child DROP INDEX fk",
    ]),
    ("drop-parent", [
        "ALTER TABLE parent DROP PRIMARY KEY",
    ]),
    ("drop-constraint-index", [
        "ALTER TABLE child DROP FOREIGN KEY fk,DROP INDEX fk",
    ]),
    ("drop-index-constraint", [
        "ALTER TABLE child DROP INDEX fk,DROP FOREIGN KEY fk",
    ]),
    ("replace-index", [
        "ALTER TABLE child DROP INDEX fk,ADD INDEX replacement(a,b)",
    ]),
    ("add-redundant", [
        "ALTER TABLE child ADD INDEX replacement(a,b)",
    ]),
    ("drop-after-replacement", [
        "ALTER TABLE child ADD INDEX replacement(a,b)",
        "ALTER TABLE child DROP INDEX fk",
    ]),
    ("drop-constraint", [
        "ALTER TABLE child DROP FOREIGN KEY fk",
    ]),
    ("rename-index", [
        "ALTER TABLE child RENAME INDEX fk TO replacement",
        "ALTER TABLE child DROP INDEX replacement",
    ]),
    ("drop-parent-off", [
        "SET foreign_key_checks=0",
        "ALTER TABLE parent DROP PRIMARY KEY",
    ]),
    ("replace-parent", [
        "ALTER TABLE parent DROP PRIMARY KEY,ADD UNIQUE KEY replacement(n)",
    ]),
    ("child-alternate", [
        "ALTER TABLE child ADD INDEX alternate(a,b)",
        "ALTER TABLE child DROP INDEX alternate",
    ]),
    ("explicit-retained", [
        "ALTER TABLE child DROP FOREIGN KEY fk,DROP INDEX fk,ADD INDEX explicit_index(a)",
        "ALTER TABLE child ADD CONSTRAINT fk FOREIGN KEY(a) REFERENCES parent(n)",
        "ALTER TABLE child ADD INDEX replacement(a,b)",
    ]),
    ("renamed-generated", [
        "ALTER TABLE child RENAME INDEX fk TO renamed",
        "ALTER TABLE child ADD INDEX replacement(a,b)",
    ]),
    ("orphan-generated", [
        "ALTER TABLE child DROP FOREIGN KEY fk",
        "ALTER TABLE child ADD INDEX replacement(a,b)",
    ]),
    ("prefix-replacement", [
        "ALTER TABLE child ADD INDEX replacement(b,a)",
    ]),
    ("unique-replacement", [
        "ALTER TABLE child ADD UNIQUE INDEX replacement(a,b)",
    ]),
    ("create-like", [
        "CREATE TABLE copied LIKE child",
        "SHOW INDEX FROM copied",
        "ALTER TABLE copied ADD INDEX replacement(a,b)",
        "SHOW INDEX FROM copied",
        "DROP TABLE copied",
    ]),
    ("rename-alone", [
        "ALTER TABLE child RENAME INDEX fk TO renamed",
    ]),
    ("visibility", [
        "ALTER TABLE child ALTER INDEX fk INVISIBLE",
        "ALTER TABLE child ADD INDEX replacement(a,b)",
    ]),
    ("primary-replacement", [
        "ALTER TABLE child ADD PRIMARY KEY(a)",
    ]),
    ("longer-generated-replacement", [
        "ALTER TABLE parent ADD COLUMN m INT,ADD UNIQUE INDEX two(n,m)",
        "ALTER TABLE child ADD CONSTRAINT longer FOREIGN KEY(a,b) REFERENCES parent(n,m)",
    ]),
    ("duplicate-generated-name", [
        "ALTER TABLE child ADD INDEX fk(a,b)",
    ]),
]


def probe(client, _):
    results = []
    for name, body in CASES:
        statements = RESET + body + OBSERVE
        result = subprocess.run(
            [*client.process.args, "--column-names", "--force"],
            input=";\n".join(statements) + ";\n",
            capture_output=True,
            text=True,
            timeout=30,
        )
        client.query("SELECT 1")
        results.append(dict(
            name=name,
            statements=statements,
            stdout=result.stdout,
            stderr=result.stderr,
            errors=[
                [int(code), state]
                for code, state in re.findall(r"ERROR (\d+) \((\w+)\)", result.stderr)
            ],
        ))
        print(name, result.stderr.strip(), flush=True)
    Path("/tmp/fsdb-fk-origin-native.json").write_text(json.dumps(results, indent=2))


if __name__ == "__main__":
    ORACLE["run"](probe)
