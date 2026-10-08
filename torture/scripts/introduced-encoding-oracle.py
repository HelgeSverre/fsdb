"""Verify exact introduced binary-literal diagnostics on native MySQL 8.4.11."""

import pathlib
import re
import runpy
import subprocess

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [
    ("SELECT _utf8mb4 X'FF'", (1300, 'HY000', "Invalid utf8mb4 character string: 'FF'")),
    ("SELECT _utf8mb4 X'C080'", (1300, 'HY000', "Invalid utf8mb4 character string: 'C080'")),
    ("SELECT _utf8mb4 X'E282'", (1300, 'HY000', "Invalid utf8mb4 character string: 'E282'")),
    ("SELECT _utf8mb3 X'F09F9880'", (1300, 'HY000', "Invalid utf8mb3 character string: 'F09F98'")),
    ("SELECT _utf8mb4 X'41FF42' WHERE FALSE", (1300, 'HY000', "Invalid utf8mb4 character string: 'FF42'")),
    ("SELECT CHARSET(_utf8mb4 X'FF')", (1300, 'HY000', "Invalid utf8mb4 character string: 'FF'")),
    ("SELECT IF(FALSE,_utf8mb4 X'FF','ok')", (1300, 'HY000', "Invalid utf8mb4 character string: 'FF'")),
    ("SELECT _utf8mb4 X'FF' COLLATE latin1_bin", (1300, 'HY000', "Invalid utf8mb4 character string: 'FF'")),
    ("SELECT _utf8 X'F09F9880'", (1300, 'HY000', "Invalid utf8mb3 character string: 'F09F98'")),
    ('SELECT _utf8mb4 0xFF', (1300, 'HY000', "Invalid utf8mb4 character string: 'FF'")),
    ('SELECT _utf8mb4 0b11111111', (1300, 'HY000', "Invalid utf8mb4 character string: 'FF'")),
    ("SELECT _utf16 X'D800'", (1300, 'HY000', "Invalid utf16 character string: 'D800'")),
    ("SELECT _utf16 X'0041D8000042'", (1300, 'HY000', "Invalid utf16 character string: 'D80000'")),
    ("SELECT _utf32 X'00110000'", (1300, 'HY000', "Invalid utf32 character string: '001100'")),
    ("SELECT _utf8mb4 X'41FF42434445'", (1300, 'HY000', "Invalid utf8mb4 character string: 'FF4243'")),
    ("SELECT _utf16le X'410000D84200'", (1300, 'HY000', "Invalid utf16le character string: '00D842'")),
    ("SELECT _utf32 X'000000410000'", (1300, 'HY000', "Invalid utf32 character string: '004100'")),
    ("SELECT _utf8mb3 X'41F09F9880FF'", (1300, 'HY000', "Invalid utf8mb3 character string: 'F09F98'")),
    ("SELECT HEX(_utf32 X'110000') AS h", (1300, 'HY000', "Invalid utf32 character string: '001100'")),
]


successes = [
    ("SELECT HEX(_utf8mb4 X'41F09F988042') AS h", 'h\n41F09F988042\n'),
    ("SELECT HEX(_utf8mb3 X'E282AC') AS h", 'h\nE282AC\n'),
    ("SELECT HEX(_utf16 X'0041D83DDE000042') AS h", 'h\n0041D83DDE000042\n'),
    ("SELECT HEX(_utf16le X'41003DD800DE4200') AS h", 'h\n41003DD800DE4200\n'),
    ("SELECT HEX(_utf32 X'000000410001F60000000042') AS h", 'h\n000000410001F60000000042\n'),
    ("SELECT HEX(_utf8mb4 X'') AS h", 'h\n\n'),
    ("SELECT HEX(_latin1 X'FF') AS h", 'h\nFF\n'),
    ("SELECT HEX(_binary X'FF') AS h", 'h\nFF\n'),
    ("SELECT HEX(_utf16 X'FFFE') AS h", 'h\nFFFE\n'),
    ("SELECT HEX(_utf16 X'41') AS h", 'h\n0041\n'),
    ("SELECT HEX(_utf16 X'0041FF') AS h", 'h\n000041FF\n'),
    ("SELECT HEX(_utf16 X'110000') AS h", 'h\n00110000\n'),
    ("SELECT HEX(_utf16 X'') AS h", 'h\n\n'),
    ("SELECT HEX(_utf16le X'41') AS h", 'h\n0041\n'),
    ("SELECT HEX(_utf16le X'0041FF') AS h", 'h\n000041FF\n'),
    ("SELECT HEX(_utf16le X'110000') AS h", 'h\n00110000\n'),
    ("SELECT HEX(_utf16le X'') AS h", 'h\n\n'),
    ("SELECT HEX(_ucs2 X'41') AS h", 'h\n0041\n'),
    ("SELECT HEX(_ucs2 X'0041FF') AS h", 'h\n000041FF\n'),
    ("SELECT HEX(_ucs2 X'110000') AS h", 'h\n00110000\n'),
    ("SELECT HEX(_ucs2 X'') AS h", 'h\n\n'),
    ("SELECT HEX(_utf32 X'41') AS h", 'h\n00000041\n'),
    ("SELECT HEX(_utf32 X'0041FF') AS h", 'h\n000041FF\n'),
    ("SELECT HEX(_utf32 X'') AS h", 'h\n\n'),
]

def verify(client, _writer):
    for query, expected in successes:
        prepared = "PREPARE valid_encoding FROM '" + query.replace("'", "''") + "'"
        arguments["verify_cases"](client, [(query, expected), (prepared + ";EXECUTE valid_encoding;DEALLOCATE PREPARE valid_encoding", expected)])
    for query, expected in cases:
        prepared = "PREPARE invalid_encoding FROM '" + query.replace("'", "''") + "'"
        for sql in [query, prepared]:
            result = subprocess.run([*client.process.args, "-e", "USE probe;" + sql], capture_output=True, text=True)
            error = re.search(r"ERROR (\d+) \(([^)]+)\) at line \d+: (.*)", result.stderr)
            actual = (int(error[1]), error[2], error[3]) if error else None
            if result.returncode == 0 or actual != expected:
                raise AssertionError(f"{sql}\nExpected {expected!r}, got {actual!r}\n{result.stderr}")
            print(sql + "\n" + result.stderr, flush=True)


if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
