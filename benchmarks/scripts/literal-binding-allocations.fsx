#r "../../src/Fsdb/bin/Debug/net10.0/Fsdb.dll"

open System
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Sql

let iterations = 1_000_000

let allocatedBytes action =
    let before = GC.GetAllocatedBytesForCurrentThread()
    for _ in 1..iterations do action ()
    GC.GetAllocatedBytesForCurrentThread() - before

let cases =
    [ "valid literal", Lit(VString "x"), None
      "invalid collation", Collate(Lit VNull, "utf8mb4_bin"),
          Some(1253, "COLLATION 'utf8mb4_bin' is not valid for CHARACTER SET 'binary'") ]

for label, expression, expected in cases do
    let verify actual =
        if actual <> expected then failwithf "%s: unexpected diagnostic %A" label actual

    for _ in 1..1000 do Expression.tryLiteralDiagnostic expression |> verify
    let cached = Expression.tryLiteralDiagnostic expression
    // The control includes the structural-equality check used by both loops.
    let control = allocatedBytes (fun () -> verify cached)
    let lookups = allocatedBytes (fun () -> Expression.tryLiteralDiagnostic expression |> verify)
    printfn "%s iterations=%d control-bytes=%d lookup-bytes=%d excess-bytes=%d"
        label iterations control lookups (lookups - control)
