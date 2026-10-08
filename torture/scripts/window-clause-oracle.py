"""Verify exact window binding diagnostics on native MySQL 8.4.11."""

import pathlib
import re
import runpy
import subprocess

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [
    ('SELECT v AS a FROM window_clause ORDER BY ROW_NUMBER() OVER (ORDER BY a)', (1054, '42S22', "Unknown column 'a' in 'window order by'")),
    ('SELECT ROW_NUMBER() OVER (ORDER BY missing) FROM window_clause', (1054, '42S22', "Unknown column 'missing' in 'window order by'")),
    ('SELECT ROW_NUMBER() OVER (PARTITION BY missing) FROM window_clause', (1054, '42S22', "Unknown column 'missing' in 'window partition by'")),
    ('SELECT ROW_NUMBER() OVER w FROM window_clause WINDOW w AS (ORDER BY missing)', (1054, '42S22', "Unknown column 'missing' in 'window order by'")),
    ('SELECT ROW_NUMBER() OVER (ORDER BY missing) FROM window_clause WHERE FALSE', (1054, '42S22', "Unknown column 'missing' in 'window order by'")),
    ('SELECT ROW_NUMBER() OVER (ORDER BY missing) FROM window_clause LIMIT 0', (1054, '42S22', "Unknown column 'missing' in 'window order by'")),
    ('SELECT SUM(v) OVER (ORDER BY missing RANGE BETWEEN 1 PRECEDING AND CURRENT ROW) FROM window_clause', (1054, '42S22', "Unknown column 'missing' in 'window order by'")),
    ('SELECT ROW_NUMBER() OVER (ORDER BY absent.v) FROM window_clause', (1054, '42S22', "Unknown column 'absent.v' in 'window order by'")),
    ('SELECT ROW_NUMBER() OVER (PARTITION BY absent.v) FROM window_clause', (1054, '42S22', "Unknown column 'absent.v' in 'window partition by'")),
    ('SELECT 1 FROM window_clause WINDOW w AS (ORDER BY missing)', (1054, '42S22', "Unknown column 'missing' in 'window order by'")),
    ('SELECT ROW_NUMBER() OVER (ORDER BY absent.v)', (1109, '42S02', "Unknown table 'absent' in window order by")),
    ('SELECT ROW_NUMBER() OVER (PARTITION BY absent.v)', (1109, '42S02', "Unknown table 'absent' in window partition by")),
    ('SELECT ROW_NUMBER() OVER (ORDER BY missing) FROM window_clause WHERE absent=1', (1054, '42S22', "Unknown column 'absent' in 'where clause'")),
    ('SELECT v AS a,ROW_NUMBER() OVER (PARTITION BY a) FROM window_clause', (1054, '42S22', "Unknown column 'a' in 'window partition by'")),
    ('SELECT ROW_NUMBER() OVER child FROM window_clause WINDOW parent AS (ORDER BY missing),child AS (parent)', (1054, '42S22', "Unknown column 'missing' in 'window order by'")),
]


def verify(client, _writer):
    arguments["verify_cases"](client, [
        ("CREATE TABLE window_clause(v INT);INSERT INTO window_clause VALUES(2),(1)", ""),
    ])
    for query, expected in cases:
        prepared = "PREPARE window_binding FROM '" + query.replace("'", "''") + "'"
        for sql in [query, prepared]:
            result = subprocess.run([*client.process.args, "-e", "USE probe;" + sql], capture_output=True, text=True)
            error = re.search(r"ERROR (\d+) \(([^)]+)\) at line \d+: (.*)", result.stderr)
            actual = (int(error[1]), error[2], error[3]) if error else None
            if result.returncode == 0 or actual != expected:
                raise AssertionError(f"{sql}\nExpected {expected!r}, got {actual!r}\n{result.stderr}")
            print(sql + "\n" + result.stderr, flush=True)


if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
