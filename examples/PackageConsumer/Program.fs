open System
open System.Threading
open Fsdb
open Fsdb.Functions
open Fsdb.Value

let run () =
    let db =
        Db.create ()
        |> Db.registerScalar "DOUBLE_IT" (function
            | [ VInt value ] -> VInt(value * 2L)
            | _ -> VNull)

    use host = Db.own db
    use changes = host.SubscribeCommits()
    use connection = host.Connect()

    let expectAffected sql values =
        match connection.Execute(sql, values) with
        | Executor.Affected _ -> ()
        | other -> failwithf "%s failed: %A" sql other

    expectAffected "CREATE TABLE items (id INT PRIMARY KEY, quantity INT)" []
    expectAffected "INSERT INTO items VALUES (?, ?)" [ VInt 1L; VInt 21L ]

    let result =
        connection.QueryValuesAsync(
            "SELECT DOUBLE_IT(quantity) AS doubled FROM items WHERE id = ?",
            [ VInt 1L ],
            CancellationToken.None
        ).GetAwaiter().GetResult()

    match result with
    | Ok([ "doubled" ], [ [ Some(VInt 42L) ] ]) ->
        let mutable event = Unchecked.defaultof<Fsdb.Storage.CommitEvent>
        if not (changes.Reader.TryRead(&event)) then failwith "Expected a committed change"
        printfn "Package consumer passed: DOUBLE_IT(quantity) = 42"
    | other -> failwithf "Unexpected query result: %A" other

run ()
