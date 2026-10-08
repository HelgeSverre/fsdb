"""Pin quoted legacy charset literal warnings against native MySQL 8.4.11."""
import pathlib
import runpy

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [
    ("SELECT HEX(_sjis'é') AS h,LENGTH(_sjis'é') AS b,CHAR_LENGTH(_sjis'é') AS n;SHOW WARNINGS", 'h\tb\tn\nC3A9\t2\t2\n'),
    ("SELECT HEX(_sjis'😀') AS h,LENGTH(_sjis'😀') AS b,CHAR_LENGTH(_sjis'😀') AS n;SHOW WARNINGS", 'h\tb\tn\nF09F9880\t4\t2\n'),
    ("SELECT HEX(_cp932'é') AS h,LENGTH(_cp932'é') AS b,CHAR_LENGTH(_cp932'é') AS n;SHOW WARNINGS", 'h\tb\tn\nC3A9\t2\t2\n'),
    ("SELECT HEX(_cp932'😀') AS h,LENGTH(_cp932'😀') AS b,CHAR_LENGTH(_cp932'😀') AS n;SHOW WARNINGS", 'h\tb\tn\nF09F9880\t4\t2\n'),
    ("SELECT HEX(_big5'é') AS h,LENGTH(_big5'é') AS b,CHAR_LENGTH(_big5'é') AS n;SHOW WARNINGS", 'h\tb\tn\nC3A9\t2\t1\n'),
    ("SELECT HEX(_big5'😀') AS h,LENGTH(_big5'😀') AS b,CHAR_LENGTH(_big5'😀') AS n;SHOW WARNINGS", "h\tb\tn\nF09F9880\t4\t4\nLevel\tCode\tMessage\nWarning\t1300\tInvalid big5 character string: 'F09F98'\nWarning\t1300\tInvalid big5 character string: 'F09F98'\nWarning\t1300\tInvalid big5 character string: 'F09F98'\n"),
    ("SELECT HEX(_gbk'é') AS h,LENGTH(_gbk'é') AS b,CHAR_LENGTH(_gbk'é') AS n;SHOW WARNINGS", 'h\tb\tn\nC3A9\t2\t1\n'),
    ("SELECT HEX(_gbk'😀') AS h,LENGTH(_gbk'😀') AS b,CHAR_LENGTH(_gbk'😀') AS n;SHOW WARNINGS", 'h\tb\tn\nF09F9880\t4\t2\n'),
    ("SELECT HEX(_ujis'é') AS h,LENGTH(_ujis'é') AS b,CHAR_LENGTH(_ujis'é') AS n;SHOW WARNINGS", 'h\tb\tn\nC3A9\t2\t1\n'),
    ("SELECT HEX(_ujis'😀') AS h,LENGTH(_ujis'😀') AS b,CHAR_LENGTH(_ujis'😀') AS n;SHOW WARNINGS", "h\tb\tn\nF09F9880\t4\t4\nLevel\tCode\tMessage\nWarning\t1300\tInvalid ujis character string: 'F09F98'\nWarning\t1300\tInvalid ujis character string: 'F09F98'\nWarning\t1300\tInvalid ujis character string: 'F09F98'\n"),
    ("SELECT HEX(_euckr'é') AS h,LENGTH(_euckr'é') AS b,CHAR_LENGTH(_euckr'é') AS n;SHOW WARNINGS", 'h\tb\tn\nC3A9\t2\t1\n'),
    ("SELECT HEX(_euckr'😀') AS h,LENGTH(_euckr'😀') AS b,CHAR_LENGTH(_euckr'😀') AS n;SHOW WARNINGS", "h\tb\tn\nF09F9880\t4\t3\nLevel\tCode\tMessage\nWarning\t1300\tInvalid euckr character string: '9880'\nWarning\t1300\tInvalid euckr character string: '9880'\nWarning\t1300\tInvalid euckr character string: '9880'\n"),
    ("SELECT HEX(_gb2312'é') AS h,LENGTH(_gb2312'é') AS b,CHAR_LENGTH(_gb2312'é') AS n;SHOW WARNINGS", 'h\tb\tn\nC3A9\t2\t1\n'),
    ("SELECT HEX(_gb2312'😀') AS h,LENGTH(_gb2312'😀') AS b,CHAR_LENGTH(_gb2312'😀') AS n;SHOW WARNINGS", "h\tb\tn\nF09F9880\t4\t4\nLevel\tCode\tMessage\nWarning\t1300\tInvalid gb2312 character string: 'F09F98'\nWarning\t1300\tInvalid gb2312 character string: 'F09F98'\nWarning\t1300\tInvalid gb2312 character string: 'F09F98'\n"),
    ("SELECT HEX(_gb18030'é') AS h,LENGTH(_gb18030'é') AS b,CHAR_LENGTH(_gb18030'é') AS n;SHOW WARNINGS", 'h\tb\tn\nC3A9\t2\t1\n'),
    ("SELECT HEX(_gb18030'😀') AS h,LENGTH(_gb18030'😀') AS b,CHAR_LENGTH(_gb18030'😀') AS n;SHOW WARNINGS", 'h\tb\tn\nF09F9880\t4\t2\n'),
]

def verify(client, _writer):
    arguments["verify_cases"](client, cases)

if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
