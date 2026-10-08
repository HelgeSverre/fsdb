"""Pin stored-program current diagnostics against native MySQL 8.4.11."""
import pathlib
import runpy
import subprocess

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
# Each script opens a fresh connection while retaining the shared schema.
cases = [('DELIMITER //\n'
  "CREATE PROCEDURE d0() BEGIN DECLARE value INT; SIGNAL SQLSTATE '01001' SET "
  "MYSQL_ERRNO=60010,MESSAGE_TEXT='routine warning'; SET value=7; END//\n"
  'DELIMITER ;\n'
  'CALL d0();SHOW WARNINGS;',
  ''),
 ('DELIMITER //\n'
  'CREATE PROCEDURE d1() BEGIN SELECT 1/0 AS n; SET @x=1; END//\n'
  'DELIMITER ;\n'
  'CALL d1();SHOW WARNINGS;',
  'n\nNULL\n'),
 ('DELIMITER //\n'
  "CREATE PROCEDURE d2() BEGIN PREPARE s FROM 'SELECT /*+ MAX_EXECUTION_TIME(1) MAX_EXECUTION_TIME(2) */ 1 "
  "AS n'; SELECT 1 AS n; END//\n"
  'DELIMITER ;\n'
  'CALL d2();SHOW WARNINGS;',
  'n\n1\n'),
 ('DELIMITER //\n'
  "CREATE PROCEDURE d3() BEGIN PREPARE s FROM 'SELECT /*+ MAX_EXECUTION_TIME(1) MAX_EXECUTION_TIME(2) */ 1 "
  "AS n'; SET @x=1; END//\n"
  'DELIMITER ;\n'
  'CALL d3();SHOW WARNINGS;',
  ''),
 ('DELIMITER //\n'
  "CREATE PROCEDURE d4() BEGIN SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT='w'; SELECT 1 AS n; END//\n"
  'DELIMITER ;\n'
  'CALL d4();SHOW WARNINGS;',
  'n\n1\n'),
 ('DELIMITER //\n'
  "CREATE PROCEDURE d5() BEGIN SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT='w'; SET @x=1; END//\n"
  'DELIMITER ;\n'
  'CALL d5();SHOW WARNINGS;',
  ''),
 ('DELIMITER //\n'
  'CREATE PROCEDURE d6() BEGIN SELECT 1/0 AS n; SELECT 1 AS n; END//\n'
  'DELIMITER ;\n'
  'CALL d6();SHOW WARNINGS;',
  'n\nNULL\nn\n1\n'),
 ('DELIMITER //\n'
  'CREATE PROCEDURE d7() BEGIN SELECT 1/0 AS n; SHOW WARNINGS; SELECT 1 AS n; END//\n'
  'DELIMITER ;\n'
  'CALL d7();SHOW WARNINGS;',
  'n\nNULL\nLevel\tCode\tMessage\nWarning\t1365\tDivision by 0\nn\n1\n'),
 ('DELIMITER //\n'
  "CREATE PROCEDURE d8() BEGIN PREPARE s FROM 'SELECT /*+ MAX_EXECUTION_TIME(1) MAX_EXECUTION_TIME(2) */ 1 "
  "AS n'; SHOW WARNINGS; END//\n"
  'DELIMITER ;\n'
  'CALL d8();SHOW WARNINGS;',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint MAX_EXECUTION_TIME(2) is ignored as conflicting/duplicated\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint MAX_EXECUTION_TIME(2) is ignored as conflicting/duplicated\n'),
 ('DELIMITER //\n'
  "CREATE PROCEDURE d_final() BEGIN SIGNAL SQLSTATE '01001' SET MYSQL_ERRNO=60010,MESSAGE_TEXT='routine "
  "warning'; END//\n"
  'DELIMITER ;\n'
  'CALL d_final();SHOW WARNINGS;',
  'Level\tCode\tMessage\nWarning\t60010\troutine warning\n')]

def verify(client, _writer):
    for sql, expected in cases:
        result = subprocess.run(
            [*client.process.args, "--comments", "--column-names", "--force"],
            input="USE probe;\n" + sql,
            capture_output=True,
            text=True,
            timeout=30,
        )
        assert result.returncode == 0, result.stderr
        # --force continues after SQL errors, so exit status alone is insufficient.
        assert result.stderr == "", result.stderr
        assert result.stdout == expected, (sql, expected, result.stdout)
    print("Stored-program diagnostic cases passed")


if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
