"""Verify text literal charset identity and lifetime on native MySQL 8.4.11."""

import pathlib
import runpy

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [
    ("SELECT CHARSET(_latin1'é'),HEX(_latin1'é'),LENGTH(_latin1'é'),CHAR_LENGTH(_latin1'é')", "CHARSET(_latin1'é')\tHEX(_latin1'é')\tLENGTH(_latin1'é')\tCHAR_LENGTH(_latin1'é')\nlatin1\tC3A9\t2\t2\n"),
    ("SELECT CHARSET(_latin1 X'C3A9'),HEX(_latin1 X'C3A9'),LENGTH(_latin1 X'C3A9'),CHAR_LENGTH(_latin1 X'C3A9')", "CHARSET(_latin1 X'C3A9')\tHEX(_latin1 X'C3A9')\tLENGTH(_latin1 X'C3A9')\tCHAR_LENGTH(_latin1 X'C3A9')\nlatin1\tC3A9\t2\t2\n"),
    ("SELECT CHARSET(_utf8mb4 X'C3A9'),HEX(_utf8mb4 X'C3A9'),LENGTH(_utf8mb4 X'C3A9'),CHAR_LENGTH(_utf8mb4 X'C3A9')", "CHARSET(_utf8mb4 X'C3A9')\tHEX(_utf8mb4 X'C3A9')\tLENGTH(_utf8mb4 X'C3A9')\tCHAR_LENGTH(_utf8mb4 X'C3A9')\nutf8mb4\tC3A9\t2\t1\n"),
    ("SELECT CHARSET('a'),COLLATION('a'),COERCIBILITY('a')", "CHARSET('a')\tCOLLATION('a')\tCOERCIBILITY('a')\nutf8mb4\tutf8mb4_0900_ai_ci\t4\n"),
    ("SELECT CHARSET(_latin1'a'),COLLATION(_latin1'a'),COERCIBILITY(_latin1'a')", "CHARSET(_latin1'a')\tCOLLATION(_latin1'a')\tCOERCIBILITY(_latin1'a')\nlatin1\tlatin1_swedish_ci\t4\n"),
    ("SELECT CHARSET(_utf8mb4'a'),COLLATION(_utf8mb4'a'),COERCIBILITY(_utf8mb4'a')", "CHARSET(_utf8mb4'a')\tCOLLATION(_utf8mb4'a')\tCOERCIBILITY(_utf8mb4'a')\nutf8mb4\tutf8mb4_0900_ai_ci\t4\n"),
    ("SELECT CHARSET(N'a'),COLLATION(N'a'),COERCIBILITY(N'a')", "CHARSET(N'a')\tCOLLATION(N'a')\tCOERCIBILITY(N'a')\nutf8mb3\tutf8mb3_general_ci\t4\n"),
    ("SELECT CHARSET(_utf8'a'),COLLATION(_utf8'a'),COERCIBILITY(_utf8'a')", "CHARSET(_utf8'a')\tCOLLATION(_utf8'a')\tCOERCIBILITY(_utf8'a')\nutf8mb3\tutf8mb3_general_ci\t4\n"),
    ("SET NAMES utf8mb4 COLLATE utf8mb4_general_ci;SELECT CHARSET('a'),COLLATION('a'),COLLATION(_utf8mb4'a'),COLLATION(N'a')", "CHARSET('a')\tCOLLATION('a')\tCOLLATION(_utf8mb4'a')\tCOLLATION(N'a')\nutf8mb4\tutf8mb4_general_ci\tutf8mb4_0900_ai_ci\tutf8mb3_general_ci\n"),
    ("SET NAMES latin1 COLLATE latin1_bin;SELECT CHARSET('a'),COLLATION('a'),COLLATION(_latin1'a'),COLLATION(_utf8mb4'a')", "CHARSET('a')\tCOLLATION('a')\tCOLLATION(_latin1'a')\tCOLLATION(_utf8mb4'a')\nlatin1\tlatin1_bin\tlatin1_swedish_ci\tutf8mb4_0900_ai_ci\n"),
    ("SELECT 'a' COLLATE 'binary'", (1253, '42000')),
    ("SELECT 'a' COLLATE latin1_bin", (1253, '42000')),
    ("SELECT _latin1'a' COLLATE utf8mb4_bin", (1253, '42000')),
    ("SELECT _latin1'a' COLLATE latin1_bin AS v", 'v\na\n'),
    ("SELECT _utf8mb4'a' COLLATE latin1_bin", (1253, '42000')),
    ("SELECT N'a' COLLATE utf8mb4_bin", (1253, '42000')),
    ("SELECT N'a' COLLATE utf8mb3_bin AS v", 'v\na\n'),
    ("SET NAMES latin1;SELECT 'a' COLLATE latin1_bin AS v", 'v\na\n'),
    ("SET NAMES latin1;SELECT 'a' COLLATE utf8mb4_bin", (1253, '42000')),
    ("SET NAMES binary;SELECT CHARSET('a'),COLLATION('a'),COERCIBILITY('a'),HEX('a')", "CHARSET('a')\tCOLLATION('a')\tCOERCIBILITY('a')\tHEX('a')\nbinary\tbinary\t4\t61\n"),
    ('SET NAMES utf8mb4 COLLATE utf8mb4_general_ci;PREPARE s FROM \'SELECT CHARSET("a"),COLLATION("a")\';SET NAMES latin1;EXECUTE s', 'CHARSET("a")\tCOLLATION("a")\nutf8mb4\tutf8mb4_general_ci\n'),
    ("SELECT CHARSET(_latin1 X'61'),COLLATION(_latin1 X'61'),HEX(_latin1 X'61')", "CHARSET(_latin1 X'61')\tCOLLATION(_latin1 X'61')\tHEX(_latin1 X'61')\nlatin1\tlatin1_swedish_ci\t61\n"),
    ("SELECT CHARSET(_utf8mb4 X'61'),COLLATION(_utf8mb4 X'61'),HEX(_utf8mb4 X'61')", "CHARSET(_utf8mb4 X'61')\tCOLLATION(_utf8mb4 X'61')\tHEX(_utf8mb4 X'61')\nutf8mb4\tutf8mb4_0900_ai_ci\t61\n"),
    ("SELECT CHARSET(_latin1 b'01100001'),COLLATION(_latin1 b'01100001')", "CHARSET(_latin1 b'01100001')\tCOLLATION(_latin1 b'01100001')\nlatin1\tlatin1_swedish_ci\n"),
    ("SELECT CHARSET(_latin1'a' 'b'),COLLATION(_latin1'a' 'b'),_latin1'a' 'b' AS v", "CHARSET(_latin1'a' 'b')\tCOLLATION(_latin1'a' 'b')\tv\nlatin1\tlatin1_swedish_ci\tab\n"),
    ("SELECT CHARSET(CONCAT(_latin1'a',_latin1'b')),COLLATION(CONCAT(_latin1'a',_latin1'b'))", "CHARSET(CONCAT(_latin1'a',_latin1'b'))\tCOLLATION(CONCAT(_latin1'a',_latin1'b'))\nlatin1\tlatin1_swedish_ci\n"),
    ("SELECT CHARSET(NAME_CONST(1,_latin1'a')),COLLATION(NAME_CONST(1,_latin1'a')),COERCIBILITY(NAME_CONST(1,_latin1'a'))", "CHARSET(NAME_CONST(1,_latin1'a'))\tCOLLATION(NAME_CONST(1,_latin1'a'))\tCOERCIBILITY(NAME_CONST(1,_latin1'a'))\nlatin1\tlatin1_swedish_ci\t4\n"),
    ("SELECT CHARSET(CAST('a' AS CHAR CHARACTER SET latin1)),COLLATION(CAST('a' AS CHAR CHARACTER SET latin1))", "CHARSET(CAST('a' AS CHAR CHARACTER SET latin1))\tCOLLATION(CAST('a' AS CHAR CHARACTER SET latin1))\nlatin1\tlatin1_swedish_ci\n"),
    ("SET NAMES latin1 COLLATE latin1_bin;CREATE VIEW literal_view AS SELECT 'a' AS v;SET NAMES utf8mb4;SELECT CHARSET(v),COLLATION(v),COERCIBILITY(v) FROM literal_view", 'CHARSET(v)\tCOLLATION(v)\tCOERCIBILITY(v)\nlatin1\tlatin1_bin\t4\n'),
    ("SET NAMES latin1 COLLATE latin1_bin;CREATE FUNCTION literal_function() RETURNS VARCHAR(64) DETERMINISTIC RETURN COLLATION('a');SET NAMES utf8mb4;SELECT literal_function()", 'literal_function()\nlatin1_bin\n'),
    ("SET NAMES utf8mb4 COLLATE utf8mb4_general_ci;SELECT CHARSET(_utf8mb4'a'),COLLATION(_utf8mb4'a'),COERCIBILITY(_utf8mb4'a')", "CHARSET(_utf8mb4'a')\tCOLLATION(_utf8mb4'a')\tCOERCIBILITY(_utf8mb4'a')\nutf8mb4\tutf8mb4_0900_ai_ci\t4\n"),
    ("SET NAMES latin1;SELECT CHARSET(_binary'a'),COLLATION(_binary'a'),COERCIBILITY(_binary'a')", "CHARSET(_binary'a')\tCOLLATION(_binary'a')\tCOERCIBILITY(_binary'a')\nbinary\tbinary\t4\n"),
    ("SELECT _latin1 X'41',N'a',_latin1'a',_latin1'a' 'b'", "_latin1 X'41'\ta\ta\ta\nA\ta\ta\tab\n"),
    ("SELECT _latin1'é' 'x',_LATIN1'a',(_latin1 X'41')", "Ã©\ta\t(_latin1 X'41')\nÃ©x\ta\tA\n"),
    ("SELECT NAME_CONST(_latin1 X'41',_latin1 X'42')", 'A\nB\n'),
    ("SELECT HEX(CONCAT(_latin1'é',_latin1'x')) AS h,LENGTH(CONCAT(_latin1'é',_latin1'x')) AS n", 'h\tn\nC3A978\t3\n'),
    ("SELECT _utf8mb3'ÅGE' = 'age' AS v,COLLATION(CONCAT(_utf8mb3'a',_utf8mb4'b')) AS c", 'v\tc\n1\tutf8mb4_0900_ai_ci\n'),
    ("CREATE TABLE intro_recovery(id INT,cs VARCHAR(20) GENERATED ALWAYS AS (HEX(_latin1'é')) STORED);INSERT INTO intro_recovery(id) VALUES(1);SELECT cs FROM intro_recovery", 'cs\nC3A9\n'),
    ("SET NAMES latin1 COLLATE latin1_bin;CREATE VIEW identity_view AS SELECT 'a' AS v;SET NAMES utf8mb4;SELECT (SELECT v FROM (SELECT 'inner' AS v) t) AS nested,v FROM identity_view", 'nested\tv\ninner\ta\n'),
    ("SET NAMES latin1 COLLATE latin1_bin;CREATE ALGORITHM=TEMPTABLE VIEW materialized_literal AS SELECT 'a' AS v;SET NAMES utf8mb4;SELECT CHARSET(v) AS cs,COLLATION(v) AS co,COERCIBILITY(v) AS c FROM materialized_literal", 'cs\tco\tc\nlatin1\tlatin1_bin\t4\n'),
    ('SELECT identity_view.*,COERCIBILITY(v) AS c FROM identity_view', 'v\tc\na\t4\n'),
    ("SET NAMES latin1 COLLATE latin1_bin;CREATE TABLE captured_generated(id INT,eq INT GENERATED ALWAYS AS ('a'='A') STORED);SET NAMES utf8mb4;INSERT INTO captured_generated(id) VALUES(1);SELECT eq FROM captured_generated", 'eq\n1\n'),
    ("SET NAMES latin1 COLLATE latin1_bin;CREATE TABLE schema_context(id INT,eq INT DEFAULT ('a'='A'),explicit_eq INT GENERATED ALWAYS AS ('a' COLLATE latin1_bin='A') STORED,CHECK('a'='A'));INSERT INTO schema_context(id) VALUES(1);SELECT eq,explicit_eq FROM schema_context", 'eq\texplicit_eq\n1\t0\n'),
    ("SELECT COLLATION_CONNECTION FROM information_schema.VIEWS WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='literal_view'", 'COLLATION_CONNECTION\nlatin1_bin\n'),
    ("SET NAMES latin1 COLLATE latin1_bin;SET character_set_client=ascii;CREATE VIEW export_view AS SELECT 'a' AS v;SET NAMES utf8mb4;SHOW CREATE VIEW export_view;SELECT CHARACTER_SET_CLIENT,COLLATION_CONNECTION,VIEW_DEFINITION FROM information_schema.VIEWS WHERE TABLE_SCHEMA='probe' AND TABLE_NAME='export_view'", "View\tCreate View\tcharacter_set_client\tcollation_connection\nexport_view\tCREATE ALGORITHM=UNDEFINED DEFINER=`root`@`localhost` SQL SECURITY DEFINER VIEW `export_view` AS select 'a' AS `v`\tascii\tlatin1_bin\nCHARACTER_SET_CLIENT\tCOLLATION_CONNECTION\tVIEW_DEFINITION\nascii\tlatin1_bin\tselect 'a' AS `v`\n"),
    ("SET NAMES latin1 COLLATE latin1_bin;CREATE VIEW grouped_literal AS SELECT 'a' AS v", ''),
    ('SELECT COLLATION(v) AS c,COERCIBILITY(v) AS n FROM grouped_literal ORDER BY v', 'c\tn\nlatin1_bin\t4\n'),
    ('SELECT COLLATION(v) AS c,COERCIBILITY(v) AS n FROM grouped_literal GROUP BY v', 'c\tn\nlatin1_bin\t4\n'),
    ('SELECT v AS alias,COERCIBILITY(v) AS n FROM grouped_literal GROUP BY alias', 'alias\tn\na\t4\n'),
    ('SELECT COUNT(*) AS n,COERCIBILITY(v) AS c FROM grouped_literal GROUP BY v', 'n\tc\n1\t4\n'),
    ("SELECT 'b' AS v,COERCIBILITY(grouped_literal.v) AS n FROM grouped_literal ORDER BY v", 'v\tn\nb\t4\n'),
    ("SELECT 'b' AS v,COERCIBILITY(grouped_literal.v) AS n FROM grouped_literal ORDER BY v COLLATE utf8mb4_bin", (1253, '42000')),
    ("SELECT 'b' AS v,COERCIBILITY(grouped_literal.v) AS n FROM grouped_literal GROUP BY v COLLATE latin1_bin", (1055, '42000')),
    ('CREATE TABLE grouping_inputs(v INT);INSERT INTO grouping_inputs VALUES(-1),(1)', ''),
    ('SELECT v FROM grouping_inputs GROUP BY ABS(v)', (1055, '42000')),
    ('SELECT ABS(v)+1 AS n FROM grouping_inputs GROUP BY ABS(v)', (1055, '42000')),
    ('SELECT v+1 AS n FROM grouping_inputs GROUP BY v ORDER BY n', 'n\n0\n2\n'),
    ('SELECT ABS(v) AS n FROM grouping_inputs GROUP BY ABS(v)', 'n\n1\n'),
    ('SELECT ABS(v) AS n,GROUPING(ABS(v)) AS g FROM grouping_inputs GROUP BY ABS(v) WITH ROLLUP', 'n\tg\n1\t0\nNULL\t1\n'),
    ("SELECT v AS alias,COERCIBILITY(v) AS n FROM grouped_literal ORDER BY CONCAT(alias,'x')", 'alias\tn\na\t4\n'),
    ('SELECT v,COERCIBILITY(v) AS n FROM grouped_literal GROUP BY v WITH ROLLUP', 'v\tn\na\t4\nNULL\t4\n'),
    ('SELECT COERCIBILITY(v) AS n,(SELECT 1) AS s FROM grouped_literal', 'n\ts\n4\t1\n'),
    ('SELECT COERCIBILITY(v) AS n FROM grouped_literal JOIN (SELECT 1 AS x) t ON 1', 'n\n4\n'),
]


def verify(client, _writer):
    arguments["verify_cases"](client, cases)


if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
