"""Pin connection-local routine hint diagnostics against native MySQL 8.4.11."""
import pathlib
import runpy
import subprocess

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
# Each script opens a fresh connection while retaining the shared schema.
cases = [('CREATE PROCEDURE life_p() SELECT /*+ MAX_EXECUTION_TIME(1) */ 1 AS n;\n'
  'SHOW WARNINGS;CALL life_p();SHOW WARNINGS;CALL life_p();SHOW WARNINGS;',
  'Level\tCode\tMessage\n'
  'Warning\t3125\tMAX_EXECUTION_TIME hint is supported by top-level standalone SELECT statements only\n'
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3125\tMAX_EXECUTION_TIME hint is supported by top-level standalone SELECT statements only\n'
  'n\n'
  '1\n'),
 ('CALL life_p();SHOW WARNINGS;CALL life_p();SHOW WARNINGS;\n'
  'CREATE TABLE invalidates_cache(n INT);CALL life_p();SHOW WARNINGS;\n'
  "ALTER PROCEDURE life_p COMMENT 'changed';CALL life_p();SHOW WARNINGS;CALL life_p();SHOW WARNINGS;",
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3125\tMAX_EXECUTION_TIME hint is supported by top-level standalone SELECT statements only\n'
  'n\n'
  '1\n'
  'n\n'
  '1\n'
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3125\tMAX_EXECUTION_TIME hint is supported by top-level standalone SELECT statements only\n'
  'n\n'
  '1\n'),
 ('DELIMITER //\n'
  'CREATE PROCEDURE life_branch() BEGIN IF 0 THEN SELECT /*+ MAX_EXECUTION_TIME(1) */ 2 AS n; END IF; SELECT '
  '1 AS n; END//\n'
  'DELIMITER ;\n'
  'SHOW WARNINGS;CALL life_branch();SHOW WARNINGS;CALL life_branch();SHOW WARNINGS;',
  'Level\tCode\tMessage\n'
  'Warning\t3125\tMAX_EXECUTION_TIME hint is supported by top-level standalone SELECT statements only\n'
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3125\tMAX_EXECUTION_TIME hint is supported by top-level standalone SELECT statements only\n'
  'n\n'
  '1\n'),
 ('CREATE FUNCTION life_f() RETURNS INT DETERMINISTIC RETURN (SELECT /*+ MAX_EXECUTION_TIME(1) */ 1);\n'
  'SHOW WARNINGS;SELECT life_f() AS n;SHOW WARNINGS;SELECT life_f() AS n;SHOW WARNINGS;',
  'Level\tCode\tMessage\n'
  'Warning\t3125\tMAX_EXECUTION_TIME hint is supported by top-level standalone SELECT statements only\n'
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3125\tMAX_EXECUTION_TIME hint is supported by top-level standalone SELECT statements only\n'
  'n\n'
  '1\n'),
 ('DELIMITER //\n'
  "CREATE PROCEDURE life_dynamic() BEGIN PREPARE s FROM 'SELECT /*+ MAX_EXECUTION_TIME(1) "
  "MAX_EXECUTION_TIME(2) */ 1 AS n'; SHOW WARNINGS; EXECUTE s; END//\n"
  'DELIMITER ;\n'
  'SHOW WARNINGS;CALL life_dynamic();SHOW WARNINGS;CALL life_dynamic();SHOW WARNINGS;',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint MAX_EXECUTION_TIME(2) is ignored as conflicting/duplicated\n'
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint MAX_EXECUTION_TIME(2) is ignored as conflicting/duplicated\n'
  'n\n'
  '1\n'),
 ('CALL life_p();SHOW WARNINGS;CALL life_p();SHOW WARNINGS;\n'
  'CREATE PROCEDURE life_other() SELECT 7;CALL life_p();SHOW WARNINGS;CALL life_p();SHOW WARNINGS;\n'
  'DROP PROCEDURE life_other;CALL life_p();SHOW WARNINGS;',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3125\tMAX_EXECUTION_TIME hint is supported by top-level standalone SELECT statements only\n'
  'n\n'
  '1\n'
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3125\tMAX_EXECUTION_TIME hint is supported by top-level standalone SELECT statements only\n'
  'n\n'
  '1\n'
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3125\tMAX_EXECUTION_TIME hint is supported by top-level standalone SELECT statements only\n'),
 ('DROP PROCEDURE life_p; CREATE PROCEDURE life_p() SELECT /*+ MAX_EXECUTION_TIME(1) */ 2 AS n;\n'
  'SHOW WARNINGS;CALL life_p();SHOW WARNINGS;CALL life_p();SHOW WARNINGS;',
  'Level\tCode\tMessage\n'
  'Warning\t3125\tMAX_EXECUTION_TIME hint is supported by top-level standalone SELECT statements only\n'
  'n\n'
  '2\n'
  'Level\tCode\tMessage\n'
  'Warning\t3125\tMAX_EXECUTION_TIME hint is supported by top-level standalone SELECT statements only\n'
  'n\n'
  '2\n')]


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
    print("Routine hint lifetime cases passed")


if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
