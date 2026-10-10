open Fsdb
open Fsdb.Functions
open Fsdb.Value

// A package consumer uses only the public embedding interface; no project
// reference or server process is needed.
let db =
    Db.create ()
    |> Db.registerScalar "DOUBLE_IT" (function
        | [ VInt value ] -> VInt(value * 2L)
        | _ -> VNull)

let connection = Db.connect db

let expectAffected sql =
    match connection.Query sql with
    | Executor.Affected _ -> ()
    | other -> failwithf "%s failed: %A" sql other

expectAffected "CREATE TABLE items (id INT PRIMARY KEY, quantity INT)"
expectAffected "INSERT INTO items VALUES (1, 21)"

match connection.Query "SELECT DOUBLE_IT(quantity) AS doubled FROM items WHERE id = 1" with
| Executor.ResultSet([ "doubled" ], [ [ Some "42" ] ]) ->
    printfn "Package consumer passed: DOUBLE_IT(quantity) = 42"
| other -> failwithf "Unexpected query result: %A" other
