/// Statement-scoped conditions exposed through MySQL's diagnostics area.
module Fsdb.Diagnostics

open System.Threading

type Level =
    | Warning
    | Error

type Condition =
    { Level: Level
      Code: int
      Message: string }

let private active = AsyncLocal<ResizeArray<Condition> option>()

let record (condition: Condition) : unit =
    active.Value |> Option.iter (fun conditions -> conditions.Add condition)

let warning code message =
    record { Level = Warning; Code = code; Message = message }

let error code message =
    record { Level = Error; Code = code; Message = message }

let capture (body: unit -> 'a) : 'a * Condition list =
    let previous = active.Value
    let conditions = ResizeArray()
    active.Value <- Some conditions

    try
        body (), List.ofSeq conditions
    finally
        active.Value <- previous
