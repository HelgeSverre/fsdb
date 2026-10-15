// Run after building Fsdb.Cli:
// dotnet fsi --nologo --exec torture/scripts/correlated-materialized-integer-benchmark.fsx
#r "../../src/Fsdb.Cli/bin/Debug/net10.0/NJsonSchema.dll"
#r "../../src/Fsdb.Cli/bin/Debug/net10.0/Fsdb.dll"

open Fsdb
open System.Diagnostics

let mutable calls = 0
let db =
    Db.create ()
    |> Db.registerScalar "TOUCH" (fun values ->
        calls <- calls + 1
        List.head values)

let connection = Db.connect db
connection.Query "CREATE TABLE outer_bounds(id INT PRIMARY KEY, x INT)" |> ignore
connection.Query "CREATE TABLE inner_rows(id INT PRIMARY KEY, k INT)" |> ignore

[ 0..99 ]
|> List.map (fun id -> sprintf "(%d,%d)" id (id * 10))
|> String.concat ","
|> fun values -> connection.Query("INSERT INTO outer_bounds VALUES" + values)
|> ignore

[ 0..999 ]
|> List.map (fun id -> sprintf "(%d,%d)" id id)
|> String.concat ","
|> fun values -> connection.Query("INSERT INTO inner_rows VALUES" + values)
|> ignore

let sql =
    "SELECT o.id,(SELECT COUNT(*) FROM (SELECT DISTINCT id,k FROM inner_rows) d "
    + "WHERE TOUCH(d.id)=d.id AND ABS(d.k)+0=o.x) FROM outer_bounds o ORDER BY o.id"

for trial in 1..3 do
    calls <- 0
    let watch = Stopwatch.StartNew()

    match connection.QueryValues(sql, []) with
    | Ok(_, rows) when rows.Length = 100 && rows |> List.forall (fun row -> row.[1] = Some(Value.VInt 1L)) ->
        printfn "trial=%d ms=%.1f residual=%d" trial watch.Elapsed.TotalMilliseconds calls
    | other -> failwithf "Unexpected result: %A" other

(connection :> System.IDisposable).Dispose()
