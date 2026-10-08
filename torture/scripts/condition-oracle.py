"""Verify recorded SQL results and condition codes on disposable MySQL 8.4."""
import argparse
import json
import pathlib
import re
import runpy
import subprocess

root = pathlib.Path(__file__).resolve().parents[1]
oracle = runpy.run_path(str(root / "scripts/fulltext-transaction-oracle.py"))

def verify(client, cases):
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
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("fixture", type=pathlib.Path, help="JSON fixture containing recorded cases")
    arguments = parser.parse_args()
    cases = json.loads(arguments.fixture.read_text())["cases"]
    oracle["run"](lambda client, _writer: verify(client, cases))
