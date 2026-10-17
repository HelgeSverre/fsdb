// Run after `just check`: dotnet fsi --nologo --exec torture/scripts/correlated-materialized-decimal-profile.fsx
#r "../../src/Fsdb.Cli/bin/Debug/net10.0/NJsonSchema.dll"
#r "../../src/Fsdb.Cli/bin/Debug/net10.0/Fsdb.dll"
open Fsdb
let mutable calls = 0
let db = Db.create () |> Db.registerScalar "TOUCH" (fun values -> calls <- calls + 1; List.head values)
let connection = Db.connect db
for sql in [
    "CREATE TABLE outer_bounds(id INT PRIMARY KEY,x DECIMAL(12,2),i BIGINT,f DOUBLE)"
    "CREATE TABLE inner_rows(id INT PRIMARY KEY,k DECIMAL(12,2))"
    "INSERT INTO outer_bounds VALUES(1,10.25,10,10.25),(2,999.00,999,999.00),(3,NULL,NULL,NULL),(4,-10.25,-10,-10.25)"
] do connection.Query sql |> ignore
[ 0..999 ]
|> List.map (fun id -> sprintf "(%d,%d.25)" id (id-500))
|> String.concat ","
|> fun values -> connection.Query("INSERT INTO inner_rows VALUES"+values)
|> ignore
for expression,bound in ["d.k","o.x";"d.k+0.00","o.x";"d.k+0.00","o.i"] do
    calls <- 0
    let sql = "SELECT o.id,(SELECT COUNT(*) FROM (SELECT DISTINCT id,k FROM inner_rows) d WHERE TOUCH(d.id)=d.id AND " + expression + "=" + bound + ") FROM outer_bounds o ORDER BY o.id"
    printfn "%s=%s: %A calls=%d" expression bound (connection.QueryValues(sql,[])) calls
calls <- 0
let inSql =
    "SELECT o.id,(SELECT COUNT(*) FROM (SELECT DISTINCT id,k FROM inner_rows) d "
    + "WHERE TOUCH(d.id)=d.id AND d.k+0.00 IN (o.x,o.x+1.00)) "
    + "FROM outer_bounds o ORDER BY o.id"
let inResult = connection.QueryValues(inSql, [])
match inResult with
| Ok(_, [ [ Some(Value.VInt 1L); Some(Value.VInt 2L) ]
          [ Some(Value.VInt 2L); Some(Value.VInt 0L) ]
          [ Some(Value.VInt 3L); Some(Value.VInt 0L) ]
          [ Some(Value.VInt 4L); Some(Value.VInt 2L) ] ]) ->
    printfn "d.k+0.00 IN (o.x,o.x+1.00): expected rows; residual calls=%d" calls
| _ -> failwithf "Unexpected decimal IN result: %A" inResult

calls <- 0
let doubleRange =
    "SELECT o.id,(SELECT COUNT(*) FROM (SELECT DISTINCT id,k FROM inner_rows) d "
    + "WHERE TOUCH(d.id)=d.id AND d.k+0.00>o.f+480e0) "
    + "FROM outer_bounds o ORDER BY o.id"
match connection.QueryValues(doubleRange, []) with
| Ok(_, [ [ Some(Value.VInt 1L); Some(Value.VInt 9L) ]
          [ Some(Value.VInt 2L); Some(Value.VInt 0L) ]
          [ Some(Value.VInt 3L); Some(Value.VInt 0L) ]
          [ Some(Value.VInt 4L); Some(Value.VInt 30L) ] ]) ->
    printfn "finite DOUBLE bound over DECIMAL expression: expected rows; residual_calls=%d" calls
| Error error -> failwithf "Unexpected DOUBLE-bound decimal range result: %A" error
| other -> failwithf "Unexpected DOUBLE-bound decimal range result: %A" other

calls <- 0
let doubleEquality =
    "SELECT o.id,(SELECT COUNT(*) FROM (SELECT DISTINCT id,k FROM inner_rows) d "
    + "WHERE TOUCH(d.id)=d.id AND d.k+0.00=o.f) "
    + "FROM outer_bounds o ORDER BY o.id"
match connection.QueryValues(doubleEquality, []) with
| Ok(_, [ [ Some(Value.VInt 1L); Some(Value.VInt 1L) ]
          [ Some(Value.VInt 2L); Some(Value.VInt 0L) ]
          [ Some(Value.VInt 3L); Some(Value.VInt 0L) ]
          [ Some(Value.VInt 4L); Some(Value.VInt 1L) ] ]) ->
    printfn "finite DOUBLE equality over DECIMAL expression: expected rows; residual_calls=%d" calls
| other -> failwithf "Unexpected DOUBLE-bound decimal equality result: %A" other
