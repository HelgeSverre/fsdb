module Fsdb.Benchmarks.ForeignKeyBenchmarks

open System
open BenchmarkDotNet.Attributes
open MySqlConnector
open Fsdb.Benchmarks.BenchServer
open Fsdb.Benchmarks.Schema

/// Compare indexed same-type FK lookup with the byte-compatible BIT/binary path.
[<MemoryDiagnoser>]
type ForeignKeyBenchmarks() =
    let mutable conn : MySqlConnection = Unchecked.defaultof<_>
    let mutable targetSession : BenchServer.TargetSession option = None
    let mutable nextValue = 1

    member _.Targets() = BenchServer.targets ()

    [<ParamsSource("Targets")>]
    member val Target = "" with get, set

    member private _.Exec(sql: string) =
        use command = conn.CreateCommand()
        command.CommandText <- sql
        command.ExecuteNonQuery() |> ignore

    [<GlobalSetup>]
    member this.Setup() =
        targetSession <- Some(BenchServer.startTarget this.Target)

        conn <- new MySqlConnection(Schema.connectionString this.Target)
        conn.Open()
        this.Exec "CREATE TABLE fk_bit_parent (id BIT(16) PRIMARY KEY)"
        this.Exec "CREATE TABLE fk_bit_child (id INT PRIMARY KEY, parent_id BIT(16), FOREIGN KEY (parent_id) REFERENCES fk_bit_parent(id))"
        this.Exec "CREATE TABLE fk_binary_child (id INT PRIMARY KEY, parent_id VARBINARY(2), FOREIGN KEY (parent_id) REFERENCES fk_bit_parent(id))"

        for first in 1 .. 100 .. 1000 do
            let values =
                [ first .. min 1000 (first + 99) ]
                |> List.map (fun id -> $"(X'{id:X4}')")
                |> String.concat ","

            this.Exec $"INSERT INTO fk_bit_parent VALUES {values}"

        this.Exec "INSERT INTO fk_bit_child VALUES (1, X'0001')"
        this.Exec "INSERT INTO fk_binary_child VALUES (1, X'0001')"
        nextValue <- 1

    [<GlobalCleanup>]
    member _.Cleanup() =
        conn.Dispose()
        targetSession |> Option.iter BenchServer.stopTarget
        targetSession <- None

    member private this.UpdateChild(table: string) =
        nextValue <- if nextValue = 1 then 2 else 1
        this.Exec $"UPDATE {table} SET parent_id = X'{nextValue:X4}' WHERE id = 1"

    [<Benchmark>]
    [<BenchmarkCategory("ForeignKey")>]
    member this.BitUpdate() = this.UpdateChild("fk_bit_child")

    [<Benchmark>]
    [<BenchmarkCategory("ForeignKey")>]
    member this.BinaryUpdate() = this.UpdateChild("fk_binary_child")
