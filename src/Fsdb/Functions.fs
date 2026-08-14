/// The scalar/aggregate function registry — every function call, including
/// built-ins like CONCAT, resolves through this one SQLite-style registry
/// via `registerScalar` rather than a hardcoded dispatch table.
module Fsdb.Functions

open System
open Fsdb.Value

/// A scalar function: its already-evaluated arguments in, one `Value` out.
/// Aggregates (M5 — SUM/COUNT/AVG accumulating across rows rather than
/// mapping one row to one value) will get their own `Aggregate` type
/// registered through a second map on the same `Registry` record once they
/// land; nothing here needs to change to make room for that.
type Scalar = Value list -> Value

/// Case-insensitive by construction: every key is upper-invariant on the
/// way in, so `lookup` just normalizes the same way rather than needing a
/// custom `IComparer`.
type Registry = Map<string, Scalar>

let empty: Registry = Map.empty

let registerScalar (name: string) (fn: Scalar) (registry: Registry) : Registry =
    Map.add (name.ToUpperInvariant()) fn registry

let lookup (name: string) (registry: Registry) : Scalar option =
    Map.tryFind (name.ToUpperInvariant()) registry

// ---------------------------------------------------------------------------
// Built-ins, registered through the same `registerScalar` API user code
// gets — no special-casing for the ones that ship in the box.
// ---------------------------------------------------------------------------

let private concatFn (args: Value list) : Value =
    // MySQL: CONCAT returns NULL if any argument is NULL.
    if args |> List.exists (function VNull -> true | _ -> false) then
        VNull
    else
        args |> List.map (toText >> Option.defaultValue "") |> String.concat "" |> VString

let private textMap (f: string -> string) : Scalar =
    function
    | [ VNull ] -> VNull
    | [ v ] -> v |> toText |> Option.defaultValue "" |> f |> VString
    | _ -> VNull

let private lengthFn: Scalar =
    function
    | [ VNull ] -> VNull
    | [ v ] -> v |> toText |> Option.defaultValue "" |> String.length |> int64 |> VInt
    | _ -> VNull

let private coalesceFn (args: Value list) : Value =
    args |> List.tryFind (function VNull -> false | _ -> true) |> Option.defaultValue VNull

let private ifNullFn: Scalar =
    function
    | [ VNull; b ] -> b
    | [ a; _ ] -> a
    | _ -> VNull

let private ifFn: Scalar =
    function
    | [ cond; a; b ] -> if truthy cond = Some true then a else b
    | _ -> VNull

let private absFn: Scalar =
    function
    | [ VNull ] -> VNull
    | [ VInt i ] -> VInt(abs i)
    | [ VDouble d ] -> VDouble(abs d)
    | [ VDecimal d ] -> VDecimal(abs d)
    | [ v ] -> VDouble(abs (toDouble v))
    | _ -> VNull

/// ROUND(x) rounds to the nearest integer; ROUND(x, n) to `n` decimal
/// places, matching (roughly) MySQL's half-away-from-zero rounding.
let private roundFn: Scalar =
    function
    | [ VNull ]
    | [ VNull; _ ] -> VNull
    | [ VInt i ] -> VInt i
    | [ VInt i; _ ] -> VInt i
    | [ VDecimal d ] -> VDecimal(Math.Round(d, MidpointRounding.AwayFromZero))
    | [ VDecimal d; VInt digits ] -> VDecimal(Math.Round(d, int digits, MidpointRounding.AwayFromZero))
    | [ VDouble d ] -> VDouble(Math.Round(d, MidpointRounding.AwayFromZero))
    | [ VDouble d; VInt digits ] -> VDouble(Math.Round(d, int digits, MidpointRounding.AwayFromZero))
    | [ v ] -> VDouble(Math.Round(toDouble v, MidpointRounding.AwayFromZero))
    | [ v; VInt digits ] -> VDouble(Math.Round(toDouble v, int digits, MidpointRounding.AwayFromZero))
    | _ -> VNull

let private modFn: Scalar =
    function
    | [ VNull; _ ]
    | [ _; VNull ] -> VNull
    | [ VInt a; VInt b ] -> if b = 0L then VNull else VInt(a % b)
    | [ a; b ] ->
        let db = toDouble b
        if db = 0.0 then VNull else VDouble(toDouble a % db)
    | _ -> VNull

let private nowFn: Scalar = fun _ -> VDateTime DateTime.Now

let builtins: Registry =
    empty
    |> registerScalar "NOW" nowFn
    |> registerScalar "CURRENT_TIMESTAMP" nowFn
    |> registerScalar "CONCAT" concatFn
    |> registerScalar "UPPER" (textMap (fun s -> s.ToUpperInvariant()))
    |> registerScalar "LOWER" (textMap (fun s -> s.ToLowerInvariant()))
    |> registerScalar "LENGTH" lengthFn
    |> registerScalar "CHAR_LENGTH" lengthFn
    |> registerScalar "COALESCE" coalesceFn
    |> registerScalar "IFNULL" ifNullFn
    |> registerScalar "IF" ifFn
    |> registerScalar "ABS" absFn
    |> registerScalar "ROUND" roundFn
    |> registerScalar "MOD" modFn
