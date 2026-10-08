"""Pin malformed text storage conversion against native MySQL 8.4.11."""
import pathlib
import runpy

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [
    ("DROP TABLE IF EXISTS encoded_target;CREATE TABLE encoded_target(v VARCHAR(20) CHARACTER SET ascii);SET sql_mode='';INSERT INTO encoded_target VALUES(_ascii X'418042');SHOW WARNINGS;SELECT HEX(v) AS h FROM encoded_target", "Level\tCode\tMessage\nWarning\t1300\tInvalid ascii character string: '8042'\nWarning\t1366\tIncorrect string value: '\\x80B' for column 'v' at row 1\nh\n41\n"),
    ("DROP TABLE IF EXISTS encoded_target;CREATE TABLE encoded_target(v VARCHAR(20) CHARACTER SET utf8mb3);SET sql_mode='';INSERT INTO encoded_target VALUES(_utf8mb3'a😀b');SHOW WARNINGS;SELECT HEX(v) AS h FROM encoded_target", "Level\tCode\tMessage\nWarning\t1287\t'utf8mb3' is deprecated and will be removed in a future release. Please use utf8mb4 instead\nWarning\t1300\tInvalid utf8mb3 character string: 'F09F98'\nWarning\t1366\tIncorrect string value: '\\xF0\\x9F\\x98\\x80b' for column 'v' at row 1\nh\n61\n"),
    ("DROP TABLE IF EXISTS encoded_target;CREATE TABLE encoded_target(v VARCHAR(20) CHARACTER SET utf8mb4);SET sql_mode='';INSERT INTO encoded_target VALUES(_utf8mb3'a😀b');SHOW WARNINGS;SELECT HEX(v) AS h FROM encoded_target", "Level\tCode\tMessage\nWarning\t1287\t'utf8mb3' is deprecated and will be removed in a future release. Please use utf8mb4 instead\nWarning\t1300\tInvalid utf8mb3 character string: 'F09F98'\nWarning\t1366\tIncorrect string value: '\\xF0\\x9F\\x98\\x80b' for column 'v' at row 1\nh\n613F3F3F3F62\n"),
    ("DROP TABLE IF EXISTS encoded_target;CREATE TABLE encoded_target(v VARCHAR(20) CHARACTER SET utf8mb4);SET sql_mode='';INSERT INTO encoded_target VALUES(_ascii X'418042');SHOW WARNINGS;SELECT HEX(v) AS h FROM encoded_target", "Level\tCode\tMessage\nWarning\t1300\tInvalid ascii character string: '8042'\nWarning\t1366\tIncorrect string value: '\\x80B' for column 'v' at row 1\nh\n413F42\n"),
    ("DROP TABLE IF EXISTS encoded_target;CREATE TABLE encoded_target(v VARCHAR(20) CHARACTER SET ascii);SET sql_mode='STRICT_TRANS_TABLES';INSERT INTO encoded_target VALUES(_ascii X'418042');SHOW WARNINGS;SELECT HEX(v) AS h FROM encoded_target", (1366, 'HY000')),
    ("DROP TABLE IF EXISTS encoded_target;CREATE TABLE encoded_target(v VARCHAR(20) CHARACTER SET utf8mb3);SET sql_mode='STRICT_TRANS_TABLES';INSERT INTO encoded_target VALUES(_utf8mb3'a😀b');SHOW WARNINGS;SELECT HEX(v) AS h FROM encoded_target", (1366, 'HY000')),
    ("DROP TABLE IF EXISTS encoded_target;CREATE TABLE encoded_target(v VARCHAR(20) CHARACTER SET utf8mb4);SET sql_mode='STRICT_TRANS_TABLES';INSERT INTO encoded_target VALUES(_utf8mb3'a😀b');SHOW WARNINGS;SELECT HEX(v) AS h FROM encoded_target", (1366, 'HY000')),
    ("DROP TABLE IF EXISTS encoded_target;CREATE TABLE encoded_target(v VARCHAR(20) CHARACTER SET utf8mb4);SET sql_mode='STRICT_TRANS_TABLES';INSERT INTO encoded_target VALUES(_ascii X'418042');SHOW WARNINGS;SELECT HEX(v) AS h FROM encoded_target", (1366, 'HY000')),
 ]

def verify(client, _writer):
    arguments["verify_cases"](client, cases)

if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
