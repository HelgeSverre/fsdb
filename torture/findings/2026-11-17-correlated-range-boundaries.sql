-- Run only against a disposable pinned MySQL oracle or an empty FSDB store.
CREATE DATABASE range_boundary_audit; USE range_boundary_audit; CREATE TABLE outer_rows(id INT); CREATE TABLE inner_rows(owner_id INT,k DOUBLE,KEY ix_k(k)); INSERT INTO outer_rows VALUES(1),(1),(2); INSERT INTO inner_rows VALUES(1,12.5),(2,12.5),(9,100),(9,101),(9,102),(9,103),(9,104),(9,105),(9,106),(9,107),(9,108),(9,109),(9,110),(9,111),(9,112),(9,113),(9,114),(9,115),(9,116),(9,117),(9,118),(9,119),(9,120),(9,121),(9,122),(9,123),(9,124),(9,125),(9,126),(9,127),(9,128),(9,129),(9,130),(9,131),(9,132),(9,133),(9,134),(9,135),(9,136),(9,137),(9,138),(9,139),(9,140),(9,141),(9,142),(9,143),(9,144),(9,145),(9,146),(9,147),(9,148),(9,149),(9,150),(9,151),(9,152),(9,153),(9,154),(9,155),(9,156),(9,157),(9,158),(9,159),(9,160),(9,161),(9,162),(9,163),(9,164),(9,165),(9,166),(9,167),(9,168),(9,169),(9,170),(9,171),(9,172),(9,173),(9,174),(9,175),(9,176),(9,177),(9,178),(9,179),(9,180),(9,181),(9,182),(9,183),(9,184),(9,185),(9,186),(9,187),(9,188),(9,189),(9,190),(9,191),(9,192),(9,193),(9,194),(9,195),(9,196),(9,197),(9,198),(9,199);

SELECT 'base' AS audit_case;
SELECT o.id,(SELECT COUNT(*) FROM inner_rows i  WHERE i.owner_id=o.id AND i.k BETWEEN '12.5x' AND '13x' ) c FROM outer_rows o ;
SHOW COUNT(*) WARNINGS;

SELECT 'outer_limit_zero' AS audit_case;
SELECT o.id,(SELECT COUNT(*) FROM inner_rows i  WHERE i.owner_id=o.id AND i.k BETWEEN '12.5x' AND '13x' ) c FROM outer_rows o LIMIT 0;
SHOW COUNT(*) WARNINGS;

SELECT 'inner_limit_zero' AS audit_case;
SELECT o.id,(SELECT COUNT(*) FROM inner_rows i  WHERE i.owner_id=o.id AND i.k BETWEEN '12.5x' AND '13x' LIMIT 0) c FROM outer_rows o ;
SHOW COUNT(*) WARNINGS;

SELECT 'broad_forced_range' AS audit_case;
SELECT o.id,(SELECT COUNT(*) FROM inner_rows i FORCE INDEX(ix_k) WHERE i.owner_id=o.id AND i.k BETWEEN '0x' AND '1000x') c FROM outer_rows o;
SHOW COUNT(*) WARNINGS;
SELECT 'empty_outer_filter' AS audit_case;
SELECT o.id,(SELECT COUNT(*) FROM inner_rows i  WHERE i.owner_id=o.id AND i.k BETWEEN '12.5x' AND '13x' ) c FROM outer_rows o WHERE o.id=9;
SHOW COUNT(*) WARNINGS;
ALTER TABLE inner_rows ADD KEY ix_owner(owner_id);
SELECT 'owner_index' AS audit_case;
SELECT o.id,(SELECT COUNT(*) FROM inner_rows i  WHERE i.owner_id=o.id AND i.k BETWEEN '12.5x' AND '13x' ) c FROM outer_rows o ;
SHOW COUNT(*) WARNINGS;

SELECT 'owner_index_one_outer_row' AS audit_case;
SELECT o.id,(SELECT COUNT(*) FROM inner_rows i WHERE i.owner_id=o.id AND i.k BETWEEN '12.5x' AND '13x') c FROM outer_rows o WHERE o.id=2;
SHOW COUNT(*) WARNINGS;
SELECT 'owner_index_zero_outer_rows' AS audit_case;
SELECT o.id,(SELECT COUNT(*) FROM inner_rows i WHERE i.owner_id=o.id AND i.k BETWEEN '12.5x' AND '13x') c FROM outer_rows o WHERE o.id=9;
SHOW COUNT(*) WARNINGS;
SELECT 'forced_range' AS audit_case;
SELECT o.id,(SELECT COUNT(*) FROM inner_rows i FORCE INDEX(ix_k) WHERE i.owner_id=o.id AND i.k BETWEEN '12.5x' AND '13x' ) c FROM outer_rows o ;
SHOW COUNT(*) WARNINGS;

SELECT 'forced_owner' AS audit_case;
SELECT o.id,(SELECT COUNT(*) FROM inner_rows i FORCE INDEX(ix_owner) WHERE i.owner_id=o.id AND i.k BETWEEN '12.5x' AND '13x' ) c FROM outer_rows o ;
SHOW COUNT(*) WARNINGS;
DELETE FROM inner_rows;
SELECT 'empty_inner_forced' AS audit_case;
SELECT o.id,(SELECT COUNT(*) FROM inner_rows i FORCE INDEX(ix_k) WHERE i.owner_id=o.id AND i.k BETWEEN '12.5x' AND '13x' ) c FROM outer_rows o ;
SHOW COUNT(*) WARNINGS;

SELECT 'empty_inner_normal' AS audit_case;
SELECT o.id,(SELECT COUNT(*) FROM inner_rows i  WHERE i.owner_id=o.id AND i.k BETWEEN '12.5x' AND '13x' ) c FROM outer_rows o ;
SHOW COUNT(*) WARNINGS;
DELETE FROM outer_rows;
SELECT 'empty_outer_table' AS audit_case;
SELECT o.id,(SELECT COUNT(*) FROM inner_rows i  WHERE i.owner_id=o.id AND i.k BETWEEN '12.5x' AND '13x' ) c FROM outer_rows o ;
SHOW COUNT(*) WARNINGS;
DROP DATABASE range_boundary_audit;
