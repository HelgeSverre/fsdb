// Build tests first, then pass its dependency directory and Fsdb.dll to dotnet fsi.
// Example: dotnet fsi --nologo --lib:tests/Fsdb.Tests/bin/Debug/net10.0 \
//   --reference:Fsdb.dll --exec torture/scripts/ordered-secondary-insert-profile.fsx
open System.Diagnostics
open Fsdb
open Fsdb.Value

for trial in 1 .. 3 do
    let db = Db.create ()
    let connection = Db.connect db
    connection.Query "CREATE TABLE probe(id INT PRIMARY KEY,rank_value INT,KEY ix_rank(rank_value))" |> ignore
    let timer = Stopwatch.StartNew()
    for id in 1 .. 1000 do
        connection.Query(sprintf "INSERT INTO probe VALUES(%d,%d)" id (id % 20)) |> ignore
    timer.Stop()
    match connection.QueryValues("SELECT COUNT(*) FROM probe", []) with
    | Ok(_, [ [ Some(VInt 1000L) ] ]) -> ()
    | result -> failwithf "trial %d did not insert 1,000 rows: %A" trial result
    printfn "trial %d: %.1f ms" trial timer.Elapsed.TotalMilliseconds
