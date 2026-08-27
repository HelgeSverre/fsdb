/// Statement-scoped conditions exposed through MySQL's diagnostics area.
module Fsdb.Diagnostics

open System.Threading

type Level =
    | Warning
    | Error
    | Note

type Condition =
    { Level: Level
      Code: int
      Message: string }

type DivisionByZeroPolicy =
    | Silent = 0
    | Warn = 1
    | Fail = 2

exception EvaluationError of code: int * message: string

let private active = AsyncLocal<ResizeArray<Condition> option>()
let private rowNumber = AsyncLocal<int option>()
let private divisionByZeroPolicy = AsyncLocal<DivisionByZeroPolicy>()

let record (condition: Condition) : unit =
    active.Value |> Option.iter (fun conditions -> conditions.Add condition)

let warning code message =
    record { Level = Warning; Code = code; Message = message }

let error code message =
    record { Level = Error; Code = code; Message = message }

let note code message =
    record { Level = Note; Code = code; Message = message }

let divisionByZero () : Result<unit, int * string> =
    let code = 1365
    let message = "Division by 0"

    match divisionByZeroPolicy.Value with
    | DivisionByZeroPolicy.Silent -> Ok()
    | DivisionByZeroPolicy.Warn ->
        warning code message
        Ok()
    | DivisionByZeroPolicy.Fail -> Result.Error(code, message)
    | unsupported -> invalidArg (nameof unsupported) "unsupported division-by-zero policy"

let withDivisionByZeroPolicy (policy: DivisionByZeroPolicy) (body: unit -> 'a) : 'a =
    let previous = divisionByZeroPolicy.Value
    divisionByZeroPolicy.Value <- policy

    try
        body ()
    finally
        divisionByZeroPolicy.Value <- previous

let currentRowNumber () = rowNumber.Value |> Option.defaultValue 1

let withRowNumber (row: int) (body: unit -> 'a) : 'a =
    let previous = rowNumber.Value
    rowNumber.Value <- Some row

    try
        body ()
    finally
        rowNumber.Value <- previous

let capture (body: unit -> 'a) : 'a * Condition list =
    let previous = active.Value
    let conditions = ResizeArray()
    active.Value <- Some conditions

    try
        body (), List.ofSeq conditions
    finally
        active.Value <- previous

let suppress (body: unit -> 'a) : 'a =
    let previous = active.Value
    active.Value <- None

    try
        body ()
    finally
        active.Value <- previous
