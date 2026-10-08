"""Inspect SHOW INDEX descriptors and CREATE DATABASE counts on MySQL 8.4."""
import pathlib
import runpy
import subprocess

oracle = runpy.run_path(str(pathlib.Path(__file__).with_name("fulltext-transaction-oracle.py")))


def inspect(client, _writer):
    arguments = [arg for arg in client.process.args if arg not in ["--batch", "--raw", "--skip-column-names"]]
    statements = [
        "USE probe",
        "CREATE TABLE index_types(n INT)",
        "SHOW INDEX FROM index_types",
        "CREATE DATABASE count_probe",
        "SELECT ROW_COUNT() AS created",
        "CREATE DATABASE IF NOT EXISTS count_probe",
        "SELECT ROW_COUNT() AS existing",
    ]
    result = subprocess.run(
        [*arguments, "--column-type-info", "-vvv", "-e", ";".join(statements) + ";"],
        capture_output=True, text=True, check=True,
    )
    print("\n".join(line.rstrip() for line in result.stdout.splitlines()), flush=True)


if __name__ == "__main__":
    oracle["run"](inspect)
