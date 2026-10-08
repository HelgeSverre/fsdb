"""Pin optimizer hint-family conflicts against native MySQL 8.4.11."""
import pathlib
import runpy
import subprocess

arguments = runpy.run_path(str(pathlib.Path(__file__).with_name("name-const-oracle.py")))
cases = [('SELECT /*+ BKA(x) NO_BKA(x) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint NO_BKA(`x` ) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'),
 ('SELECT /*+ BNL(x) NO_BNL(x) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint NO_BNL(`x` ) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BNL hint\n'),
 ('SELECT /*+ HASH_JOIN(x) NO_HASH_JOIN(x) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint NO_HASH_JOIN(`x` ) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for HASH_JOIN hint\n'),
 ('SELECT /*+ MERGE(x) NO_MERGE(x) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint NO_MERGE(`x` ) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for MERGE hint\n'),
 ('SELECT /*+ DERIVED_CONDITION_PUSHDOWN(x) NO_DERIVED_CONDITION_PUSHDOWN(x) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint NO_DERIVED_CONDITION_PUSHDOWN(`x` ) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for DERIVED_CONDITION_PUSHDOWN hint\n'),
 ('SELECT /*+ MRR(x) NO_MRR(x) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint NO_MRR(`x` ) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for MRR hint\n'),
 ('SELECT /*+ INDEX_MERGE(x) NO_INDEX_MERGE(x) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint NO_INDEX_MERGE(`x` ) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for INDEX_MERGE hint\n'),
 ('SELECT /*+ SKIP_SCAN(x) NO_SKIP_SCAN(x) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint NO_SKIP_SCAN(`x` ) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for SKIP_SCAN hint\n'),
 ('SELECT /*+ INDEX(x) NO_INDEX(x) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint NO_INDEX(`x` ) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for INDEX hint\n'),
 ('SELECT /*+ JOIN_INDEX(x) NO_JOIN_INDEX(x) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint NO_JOIN_INDEX(`x` ) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for JOIN_INDEX hint\n'),
 ('SELECT /*+ GROUP_INDEX(x) NO_GROUP_INDEX(x) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint NO_GROUP_INDEX(`x` ) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for GROUP_INDEX hint\n'),
 ('SELECT /*+ ORDER_INDEX(x) NO_ORDER_INDEX(x) */ 1 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint NO_ORDER_INDEX(`x` ) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for ORDER_INDEX hint\n'),
 ('SELECT /*+ JOIN_PREFIX(a,b) JOIN_PREFIX(b,a) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint JOIN_PREFIX( `b`,`a`) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ JOIN_SUFFIX(a,b) JOIN_SUFFIX(b,a) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint JOIN_SUFFIX( `b`,`a`) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ JOIN_ORDER(a,b) JOIN_ORDER(b,a) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS', ''),
 ('SELECT /*+ JOIN_PREFIX(a,a) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS', ''),
 ('SELECT /*+ JOIN_ORDER(a,a) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS', ''),
 ('SELECT /*+ JOIN_PREFIX(x) JOIN_PREFIX(y) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint JOIN_PREFIX( `y`) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_PREFIX hint\n'),
 ('SELECT /*+ JOIN_ORDER(x) JOIN_ORDER(y) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'
  'Warning\t3128\tUnresolved name `y` for JOIN_ORDER hint\n'),
 ('SELECT /*+ JOIN_SUFFIX(x) JOIN_SUFFIX(y) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint JOIN_SUFFIX( `y`) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_SUFFIX hint\n'),
 ('SELECT /*+ JOIN_FIXED_ORDER() JOIN_FIXED_ORDER() */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint JOIN_FIXED_ORDER( ) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ JOIN_FIXED_ORDER() JOIN_PREFIX(a,b) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint JOIN_PREFIX( `a`,`b`) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ JOIN_PREFIX(a,b) JOIN_FIXED_ORDER() */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint JOIN_FIXED_ORDER( ) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ JOIN_PREFIX(a) JOIN_SUFFIX(a) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS', ''),
 ('SELECT /*+ JOIN_ORDER(a,b) JOIN_PREFIX(b) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS', ''),
 ('SELECT /*+ JOIN_FIXED_ORDER(@missing) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3127\tQuery block name `missing` is not found for JOIN_FIXED_ORDER hint\n'),
 ('SELECT /*+ SEMIJOIN(@missing FIRSTMATCH) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3127\tQuery block name `missing` is not found for SEMIJOIN hint\n'),
 ('SELECT /*+ SUBQUERY(@missing MATERIALIZATION) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3127\tQuery block name `missing` is not found for SUBQUERY hint\n'),
 ('SELECT /*+ INDEX(t i1) JOIN_INDEX(t i2) */ 1 AS n FROM t;SHOW WARNINGS', ''),
 ('SELECT /*+ JOIN_INDEX(t i1) GROUP_INDEX(t i2) */ 1 AS n FROM t;SHOW WARNINGS', ''),
 ('SELECT /*+ INDEX_MERGE(t i1) INDEX_MERGE(t i2) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3614\tInvalid number of arguments for hint INDEX_MERGE(`t`  `i1`)\n'
  'Warning\t3614\tInvalid number of arguments for hint INDEX_MERGE(`t`  `i2`)\n'),
 ('SELECT /*+ SKIP_SCAN(t i1) SKIP_SCAN(t i2) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint SKIP_SCAN(`t`  `i2`) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ MRR(t i1) MRR(t i2) */ 1 AS n FROM t;SHOW WARNINGS', ''),
 ('SELECT /*+ NO_ICP(t i1) NO_ICP(t i2) */ 1 AS n FROM t;SHOW WARNINGS', ''),
 ('SELECT /*+ NO_RANGE_OPTIMIZATION(t i1) NO_RANGE_OPTIMIZATION(t i2) */ 1 AS n FROM t;SHOW WARNINGS', ''),
 ('SELECT /*+ BKA(@missing x,y) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3127\tQuery block name `missing` is not found for BKA hint\n'),
 ('SELECT /*+ QB_NAME(q) BKA(x@q) BKA(x@q) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint BKA(`x`@`q` ) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`q` for BKA hint\n'),
 ('SELECT /*+ INDEX_MERGE(t i1) NO_INDEX_MERGE(t i2) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3614\tInvalid number of arguments for hint INDEX_MERGE(`t`  `i1`)\n'),
 ('SELECT /*+ INDEX_MERGE(t i1,i2) INDEX_MERGE(t i1,i2) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint INDEX_MERGE(`t`  `i1`, `i2`) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ MRR(t i1) NO_MRR(t i1) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint NO_MRR(`t` `i1` ) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ MRR(t i1,i2) NO_MRR(t i2) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint NO_MRR(`t` `i2` ) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ MRR(t i1) MRR(t i1,i2) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint MRR(`t` `i1` ) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ MRR(x i1,i2) MRR(x i2,i3) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint MRR(`x` `i2` ) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` `i1` for MRR hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` `i2` for MRR hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` `i3` for MRR hint\n'),
 ('SELECT /*+ MRR(t) MRR(t i1) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint MRR(`t` `i1` ) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ NO_MRR(t) MRR(t i1) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint MRR(`t` `i1` ) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ MRR(t i1) MRR(t) */ 1 AS n FROM t;SHOW WARNINGS', ''),
 ('SELECT /*+ JOIN_ORDER() JOIN_ORDER() */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS', ''),
 ('SELECT /*+ JOIN_ORDER(x,y) JOIN_ORDER(x,z) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'
  'Warning\t3128\tUnresolved name `x` for JOIN_ORDER hint\n'),
 ('SELECT /*+ JOIN_PREFIX(x,x) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_PREFIX hint\n'),
 ('SELECT /*+ QB_NAME(q) JOIN_PREFIX(@q a,b) JOIN_PREFIX(@q b,a) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint JOIN_PREFIX(@`q` `b`,`a`) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ QB_NAME(q) JOIN_PREFIX(x@q) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x`@`q` for JOIN_PREFIX hint\n'),
 ('SELECT /*+ JOIN_PREFIX(x@missing) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x`@`missing` for JOIN_PREFIX hint\n'),
 ('SELECT /*+ JOIN_PREFIX(@missing x) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3127\tQuery block name `missing` is not found for JOIN_PREFIX hint\n'),
 ('SELECT /*+ JOIN_FIXED_ORDER(@q) QB_NAME(q) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3127\tQuery block name `q` is not found for JOIN_FIXED_ORDER hint\n'),
 ('SELECT /*+ SEMIJOIN(FIRSTMATCH) SUBQUERY(MATERIALIZATION) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint SUBQUERY(  MATERIALIZATION) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ SUBQUERY(MATERIALIZATION) SEMIJOIN(FIRSTMATCH) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint SEMIJOIN(  FIRSTMATCH) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ SEMIJOIN(FIRSTMATCH) SEMIJOIN(LOOSESCAN) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint SEMIJOIN(  LOOSESCAN) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ NO_SEMIJOIN(MATERIALIZATION) SEMIJOIN(FIRSTMATCH) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint SEMIJOIN(  FIRSTMATCH) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ SUBQUERY(MATERIALIZATION) SUBQUERY(INTOEXISTS) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint SUBQUERY(  INTOEXISTS) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ JOIN_ORDER(a,b) JOIN_FIXED_ORDER() */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint JOIN_FIXED_ORDER( ) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ JOIN_FIXED_ORDER() JOIN_ORDER(a,b) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint JOIN_ORDER( `a`,`b`) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ BKA(t@q) QB_NAME(q) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3127\tQuery block name `q` is not found for BKA hint\n'),
 ('SELECT /*+ BKA(@q t) QB_NAME(q) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3127\tQuery block name `q` is not found for BKA hint\n'),
 ('SELECT /*+ SEMIJOIN(@q FIRSTMATCH) QB_NAME(q) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3127\tQuery block name `q` is not found for SEMIJOIN hint\n'),
 ('SELECT /*+ QB_NAME(q) JOIN_PREFIX(@q x) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x` for JOIN_PREFIX hint\n'),
 ('SELECT /*+ QB_NAME(q) JOIN_PREFIX(a@q) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS', ''),
 ('SELECT /*+ MRR(x i1) NO_MRR(x i2) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` `i1` for MRR hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` `i2` for NO_MRR hint\n'),
 ('SELECT /*+ NO_MRR(x i1) MRR(x i2) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` `i1` for NO_MRR hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` `i2` for MRR hint\n'),
 ('SELECT /*+ MRR(t i1,i1) */ 1 AS n FROM t;SHOW WARNINGS', ''),
 ('SELECT /*+ NO_ICP(t i1) NO_ICP(t i1) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint NO_ICP(`t` `i1` ) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ NO_RANGE_OPTIMIZATION(t i1) NO_RANGE_OPTIMIZATION(t i1) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint NO_RANGE_OPTIMIZATION(`t` `i1` ) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ INDEX_MERGE(t missing) BKA(x) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3614\tInvalid number of arguments for hint INDEX_MERGE(`t`  `missing`)\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'),
 ('SELECT /*+ JOIN_PREFIX(@q a) QB_NAME(q) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3127\tQuery block name `q` is not found for JOIN_PREFIX hint\n')]


cases += [('SELECT /*+ QB_NAME(q) SEMIJOIN(@q FIRSTMATCH) SEMIJOIN(@q LOOSESCAN) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint SEMIJOIN(@`q`  LOOSESCAN) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ QB_NAME(q) SUBQUERY(@q MATERIALIZATION) SUBQUERY(@q INTOEXISTS) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint SUBQUERY(@`q`  INTOEXISTS) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ SEMIJOIN() SEMIJOIN() */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint SEMIJOIN( ) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ SEMIJOIN(FIRSTMATCH,MATERIALIZATION) SEMIJOIN(DUPSWEEDOUT,LOOSESCAN) */ 1 AS n FROM t;SHOW '
  'WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint SEMIJOIN(  LOOSESCAN, DUPSWEEDOUT) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ INDEX_MERGE(t i1,i1) */ 1 AS n FROM t;SHOW WARNINGS', ''),
 ('SELECT /*+ MRR(x i1) MRR(x) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for MRR hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` `i1` for MRR hint\n'),
 ('SELECT /*+ MRR(x i1) NO_MRR(x) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for NO_MRR hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` `i1` for MRR hint\n'),
 ('SELECT /*+ BKA(x) JOIN_PREFIX(y) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y` for JOIN_PREFIX hint\n'),
 ('SELECT /*+ JOIN_PREFIX(y) BKA(x) */ 1 AS n FROM t a JOIN t b;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y` for JOIN_PREFIX hint\n'),
 ('SELECT /*+ MRR(x i1) BKA(x) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` `i1` for MRR hint\n'),
 ('SELECT /*+ MRR(x i1) BKA(y) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` `i1` for MRR hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#1` for BKA hint\n'),
 ('SELECT /*+ MRR(x i1) NO_INDEX(x missing) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for NO_INDEX hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` `i1` for MRR hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` `missing` for NO_INDEX hint\n'),
 ('SELECT /*+ NO_INDEX(x missing) BKA(x) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for NO_INDEX hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` `missing` for NO_INDEX hint\n'),
 ('SELECT /*+ NO_INDEX(y missing) BKA(x) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `y`@`select#1` for NO_INDEX hint\n'
  'Warning\t3128\tUnresolved name `y`@`select#1` `missing` for NO_INDEX hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'),
 ('SELECT /*+ NO_MRR(x) BKA(x) NO_BNL(x) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for NO_BNL hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for NO_MRR hint\n')]

cases += [('SELECT /*+ INDEX_MERGE(@missing t i1) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3127\tQuery block name `missing` is not found for INDEX_MERGE hint\n'),
 ('SELECT /*+ INDEX_MERGE(t@q i1) QB_NAME(q) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3127\tQuery block name `q` is not found for INDEX_MERGE hint\n'),
 ('SELECT /*+ QB_NAME(q) INDEX_MERGE(t@q i1) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3614\tInvalid number of arguments for hint INDEX_MERGE(`t`@`q`  `i1`)\n'),
 ('SELECT /*+ BKA() BKA(x) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint BKA(`x` ) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ NO_BKA() BKA(x) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint BKA(`x` ) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ BKA(x) NO_BKA() */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'),
 ('SELECT /*+ QB_NAME(q) BKA(x) */ (SELECT /*+ QB_NAME(q) BKA(y) */ 1) AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint QB_NAME(`q`) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`q` for BKA hint\n'),
 ('SELECT /*+ QB_NAME(outerq) */ (SELECT /*+ BKA(t@outerq) */ 1) AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3127\tQuery block name `outerq` is not found for BKA hint\n'),
 ('SELECT (SELECT /*+ QB_NAME(q) BKA(x) */ 1) AS n FROM (SELECT /*+ QB_NAME(q) BKA(y) */ 1) a;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint QB_NAME(`q`) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `y`@`select#3` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`q` for BKA hint\n'),
 ('SELECT /*+ BKA(t@q) */ 1 AS n FROM (SELECT /*+ QB_NAME(q) */ 1 FROM t) a;SHOW WARNINGS', ''),
 ('SELECT /*+ QB_NAME(rootq) QB_NAME(root_rejected) */ (SELECT /*+ QB_NAME(childq) QB_NAME(child_rejected) '
  '*/ 1) AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint QB_NAME(`child_rejected`) is ignored as conflicting/duplicated\n'
  'Warning\t3126\tHint QB_NAME(`root_rejected`) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ MAX_EXECUTION_TIME(10000) MAX_EXECUTION_TIME(2) */ (SELECT /*+ QB_NAME(q) QB_NAME(r) */ 1) AS '
  'n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint QB_NAME(`r`) is ignored as conflicting/duplicated\n'
  'Warning\t3126\tHint MAX_EXECUTION_TIME(2) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ QB_NAME(q) QB_NAME(r) */ (SELECT /*+ MAX_EXECUTION_TIME(1) */ 1) AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3125\tMAX_EXECUTION_TIME hint is supported by top-level standalone SELECT statements only\n'
  'Warning\t3126\tHint QB_NAME(`r`) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ BKA(x) */ 1 AS n UNION ALL SELECT /*+ BKA(x) */ 2 AS n UNION ALL SELECT /*+ BKA(x) */ 3 AS n '
  'UNION ALL SELECT /*+ BKA(x) */ 4 AS n UNION ALL SELECT /*+ BKA(x) */ 5 AS n UNION ALL SELECT /*+ BKA(x) '
  '*/ 6 AS n UNION ALL SELECT /*+ BKA(x) */ 7 AS n UNION ALL SELECT /*+ BKA(x) */ 8 AS n UNION ALL SELECT '
  '/*+ BKA(x) */ 9 AS n UNION ALL SELECT /*+ BKA(x) */ 10 AS n UNION ALL SELECT /*+ BKA(x) */ 11 AS n UNION '
  'ALL SELECT /*+ BKA(x) */ 12 AS n;SHOW WARNINGS',
  'n\n'
  '1\n'
  '2\n'
  '3\n'
  '4\n'
  '5\n'
  '6\n'
  '7\n'
  '8\n'
  '9\n'
  '10\n'
  '11\n'
  '12\n'
  'Level\tCode\tMessage\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#2` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#3` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#4` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#5` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#6` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#7` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#8` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#9` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#a` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#b` for BKA hint\n'
  'Warning\t3128\tUnresolved name `x`@`select#c` for BKA hint\n'),
 ('WITH c AS (SELECT /*+ QB_NAME(q) BKA(y) */ 1 AS n) SELECT /*+ QB_NAME(q) BKA(x) */ n FROM c;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3126\tHint QB_NAME(`q`) is ignored as conflicting/duplicated\n'
  'Warning\t3128\tUnresolved name `x`@`select#1` for BKA hint\n'
  'Warning\t3128\tUnresolved name `y`@`q` for BKA hint\n'),
 ('SELECT (SELECT /*+ BKA(t@q) */ 1) AS n FROM (SELECT /*+ QB_NAME(q) */ 1 AS n FROM t) a;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3127\tQuery block name `q` is not found for BKA hint\n'),
 ('SELECT /*+ QB_NAME(q) BKA(@q t) BKA(@q t) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint BKA(`t` ) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ QB_NAME(q) BKA() BKA(@q x) */ 1 AS n FROM t;SHOW WARNINGS',
  'Level\tCode\tMessage\nWarning\t3126\tHint BKA(`x` ) is ignored as conflicting/duplicated\n'),
 ('SELECT /*+ MAX_EXECUTION_TIME(10000) MAX_EXECUTION_TIME(2) */ (SELECT /*+ MAX_EXECUTION_TIME(1) */ 1) AS '
  'n;SHOW WARNINGS',
  'n\n'
  '1\n'
  'Level\tCode\tMessage\n'
  'Warning\t3125\tMAX_EXECUTION_TIME hint is supported by top-level standalone SELECT statements only\n'
  'Warning\t3126\tHint MAX_EXECUTION_TIME(2) is ignored as conflicting/duplicated\n')]

def verify(client, _writer):
    setup = subprocess.run(
        [*client.process.args, "-e", "USE probe;CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"],
        capture_output=True, text=True, check=True,
    )
    assert not setup.stderr, setup.stderr
    arguments["verify_cases"](client, cases)


if __name__ == "__main__":
    arguments["oracle"]["run"](verify)
