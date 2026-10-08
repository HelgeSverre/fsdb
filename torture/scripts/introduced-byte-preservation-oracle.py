"""Pin introduced byte preservation, warnings, and materialization on MySQL 8.4.11."""

import pathlib
import runpy

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [
    ("SELECT HEX(_ascii X'80') AS h,LENGTH(_ascii X'80') AS n;SHOW WARNINGS", "h\tn\n80\t1\nLevel\tCode\tMessage\nWarning\t1300\tInvalid ascii character string: '80'\nWarning\t1300\tInvalid ascii character string: '80'\n"),
    ("SELECT HEX(_ucs2 X'D800') AS h,LENGTH(_ucs2 X'D800') AS n;SHOW WARNINGS", "h\tn\nD800\t2\nLevel\tCode\tMessage\nWarning\t1287\t'ucs2' is deprecated and will be removed in a future release. Please use utf8mb4 instead\nWarning\t1287\t'ucs2' is deprecated and will be removed in a future release. Please use utf8mb4 instead\n"),
    ("SELECT HEX(_utf8mb3'😀') AS h,LENGTH(_utf8mb3'😀') AS n;SHOW WARNINGS", "h\tn\nF09F9880\t4\nLevel\tCode\tMessage\nWarning\t1287\t'utf8mb3' is deprecated and will be removed in a future release. Please use utf8mb4 instead\nWarning\t1287\t'utf8mb3' is deprecated and will be removed in a future release. Please use utf8mb4 instead\nWarning\t1300\tInvalid utf8mb3 character string: 'F09F98'\nWarning\t1300\tInvalid utf8mb3 character string: 'F09F98'\n"),
    ("SELECT HEX(N'😀') AS h,LENGTH(N'😀') AS n;SHOW WARNINGS", "h\tn\nF09F9880\t4\nLevel\tCode\tMessage\nWarning\t3720\tNATIONAL/NCHAR/NVARCHAR implies the character set UTF8MB3, which will be replaced by UTF8MB4 in a future release. Please consider using CHAR(x) CHARACTER SET UTF8MB4 in order to be unambiguous.\nWarning\t3720\tNATIONAL/NCHAR/NVARCHAR implies the character set UTF8MB3, which will be replaced by UTF8MB4 in a future release. Please consider using CHAR(x) CHARACTER SET UTF8MB4 in order to be unambiguous.\nWarning\t1300\tInvalid utf8mb3 character string: 'F09F98'\nWarning\t1300\tInvalid utf8mb3 character string: 'F09F98'\n"),
    ("SELECT HEX(_ascii'é') AS h,LENGTH(_ascii'é') AS n;SHOW WARNINGS", "h\tn\nC3A9\t2\nLevel\tCode\tMessage\nWarning\t1300\tInvalid ascii character string: 'C3A9'\nWarning\t1300\tInvalid ascii character string: 'C3A9'\n"),
    ("SELECT HEX(_utf8mb3'a😀b') AS h;SHOW WARNINGS", "h\n61F09F988062\nLevel\tCode\tMessage\nWarning\t1287\t'utf8mb3' is deprecated and will be removed in a future release. Please use utf8mb4 instead\nWarning\t1300\tInvalid utf8mb3 character string: 'F09F98'\n"),
    ("PREPARE p FROM 'SELECT HEX(_ascii X''80'') AS h';SHOW WARNINGS;EXECUTE p;SHOW WARNINGS;EXECUTE p;SHOW WARNINGS", "Level\tCode\tMessage\nWarning\t1300\tInvalid ascii character string: '80'\nh\n80\nh\n80\n"),
    ("PREPARE p FROM 'SELECT HEX(_utf8mb3''😀'') AS h';SHOW WARNINGS;EXECUTE p;SHOW WARNINGS;EXECUTE p;SHOW WARNINGS", "Level\tCode\tMessage\nWarning\t1287\t'utf8mb3' is deprecated and will be removed in a future release. Please use utf8mb4 instead\nh\n3F\nh\n3F\n"),
    ("PREPARE p FROM 'SELECT HEX(N''😀'') AS h';SHOW WARNINGS;EXECUTE p;SHOW WARNINGS;EXECUTE p;SHOW WARNINGS", 'Level\tCode\tMessage\nWarning\t3720\tNATIONAL/NCHAR/NVARCHAR implies the character set UTF8MB3, which will be replaced by UTF8MB4 in a future release. Please consider using CHAR(x) CHARACTER SET UTF8MB4 in order to be unambiguous.\nh\n3F\nh\n3F\n'),
    ("PREPARE p FROM 'SELECT HEX(_ucs2 X''D800'') AS h';SHOW WARNINGS;EXECUTE p;SHOW WARNINGS;EXECUTE p;SHOW WARNINGS", "Level\tCode\tMessage\nWarning\t1287\t'ucs2' is deprecated and will be removed in a future release. Please use utf8mb4 instead\nh\nD800\nh\nD800\n"),
    ("SET @v=_ascii X'80';SHOW WARNINGS;SELECT HEX(@v) AS h;SHOW WARNINGS", "Level\tCode\tMessage\nWarning\t1300\tInvalid ascii character string: '80'\nh\n80\n"),
    ("SELECT HEX(v) AS h FROM (SELECT _ascii X'80' AS v) d;SHOW WARNINGS", "h\n\nLevel\tCode\tMessage\nWarning\t1300\tInvalid ascii character string: '80'\nWarning\t1366\tIncorrect string value: '\\x80' for column 'v' at row 1\n"),
]


def verify(client, _writer):
    arguments["verify_cases"](client, cases)


if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
