"""Verify trigger warning lifetimes, failure conditions, and RESIGNAL on MySQL 8.4."""
import json
import pathlib
import re
import runpy
import subprocess

root = pathlib.Path(__file__).resolve().parents[1]
oracle = runpy.run_path(str(root / "scripts/fulltext-transaction-oracle.py"))
cases = json.loads((root / "findings/2026-10-08-trigger-warnings-native.json").read_text())["cases"]

def verify(client, _writer):
    for case in cases:
        sql = "DELIMITER //\n" + "//\n".join(case["statements"]) + "//\n"
        result = subprocess.run(
            [*client.process.args, "--column-names", "--force"],
            input=sql, capture_output=True, text=True, timeout=30,
        )
        assert result.returncode == 0, (case["name"], result.stderr)
        errors = [[int(code), state] for code, state in re.findall(r"ERROR (\d+) \((\w+)\)", result.stderr)]
        assert errors == case["errors"], (case["name"], errors, case["errors"])
        oracle["expect"](case["name"], result.stdout, case["stdout"])

if __name__ == "__main__":
    oracle["run"](verify)
