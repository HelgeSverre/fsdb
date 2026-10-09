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
      State: string
      Message: string
      Information: Map<string, string> }

type DivisionByZeroPolicy =
    | Silent = 0
    | Warn = 1
    | Fail = 2

exception EvaluationError of code: int * message: string
exception RaisedCondition of SqlState.Error

type private Collector =
    { Conditions: ResizeArray<Condition>
      Limit: int
      mutable Count: int
      mutable ErrorCount: int }

let private active = AsyncLocal<Collector option>()
let private deferred = AsyncLocal<Collector option>()
let private retainedLimit = AsyncLocal<int option>()
let private rowNumber = AsyncLocal<int option>()
let private divisionByZeroPolicy = AsyncLocal<DivisionByZeroPolicy>()
let private strictNumericConversion = AsyncLocal<bool>()

let private boundedCondition (condition: Condition) =
    if condition.Message.Length > 511 then
        { condition with Message = condition.Message.Substring(0, 511) }
    else condition

let record (condition: Condition) : unit =
    active.Value |> Option.iter (fun collector ->
        collector.Count <- collector.Count + 1
        if condition.Level = Error then collector.ErrorCount <- collector.ErrorCount + 1
        if collector.Conditions.Count < collector.Limit then collector.Conditions.Add(boundedCondition condition))

let withRetainedLimit limit body =
    DynamicScope.withValue retainedLimit (Some(max 0 (min 65535 limit))) body

let warning code message =
    record
        { Level = Warning
          Code = code
          State = SqlState.forCode code
          Message = message
          Information = Map.empty }

let numericConversion kind text =
    let message = sprintf "Truncated incorrect %s value: '%s'" kind text
    if strictNumericConversion.Value then raise (RaisedCondition(SqlState.create 1292 message))
    else warning 1292 message

let withStrictNumericConversion strict body =
    DynamicScope.withValue strictNumericConversion strict body

let error code message =
    record
        { Level = Error
          Code = code
          State = SqlState.forCode code
          Message = message
          Information = Map.empty }

let note code message =
    record
        { Level = Note
          Code = code
          State = SqlState.forCode code
          Message = message
          Information = Map.empty }

let deprecatedUtf8Alias () =
    warning
        3719
        "'utf8' is currently an alias for the character set UTF8MB3, but will be an alias for UTF8MB4 in a future release. Please consider using UTF8MB4 in order to be unambiguous."

let deprecatedUtf8mb3 () =
    warning
        1287
        "'utf8mb3' is deprecated and will be removed in a future release. Please use utf8mb4 instead"

let fromErrorWithLevel level (error: SqlState.Error) =
    { Level = level
      Code = error.Code
      State = error.State
      Message = error.Message
      Information = error.Information }

let fromError error = fromErrorWithLevel Error error
let fromWarning error = fromErrorWithLevel Warning error

let invalidConditionNumber =
    { Level = Error
      Code = 1758
      State = SqlState.forCode 1758
      Message = "Invalid condition number"
      Information = Map.empty }

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
    DynamicScope.withValue divisionByZeroPolicy policy body

let currentRowNumber () = rowNumber.Value |> Option.defaultValue 1

let withRowNumber (row: int) (body: unit -> 'a) : 'a =
    DynamicScope.withValue rowNumber (Some row) body

type CapturedConditions =
    { BeforeError: Condition list
      AfterError: Condition list
      Count: int
      ErrorCount: int
      Limit: int }

let conditions captured = (captured.BeforeError @ captured.AfterError) |> List.truncate captured.Limit

let complete error captured =
    (captured.BeforeError
    @ (error |> Option.map (fromError >> boundedCondition >> List.singleton) |> Option.defaultValue [])
    @ captured.AfterError)
    |> List.truncate captured.Limit

let captureStatement (body: unit -> 'a) : 'a * CapturedConditions =
    let limit = retainedLimit.Value |> Option.defaultValue 1024
    let collector () =
        { Conditions = ResizeArray()
          Limit = limit
          Count = 0
          ErrorCount = 0 }
    let beforeError = collector ()
    let afterError = collector ()
    let result =
        DynamicScope.withValue deferred (Some afterError) (fun () ->
            DynamicScope.withValue active (Some beforeError) body)
    result,
    { BeforeError = List.ofSeq beforeError.Conditions
      AfterError = List.ofSeq afterError.Conditions
      Count = beforeError.Count + afterError.Count
      ErrorCount = beforeError.ErrorCount + afterError.ErrorCount
      Limit = limit }

let capture (body: unit -> 'a) : 'a * Condition list =
    let result, captured = captureStatement body
    result, conditions captured

/// Captures conversions reached after a failure but before execution unwinds.
let afterError (body: unit -> 'a) : 'a =
    DynamicScope.withValue active deferred.Value body

let suppress (body: unit -> 'a) : 'a =
    withStrictNumericConversion false (fun () ->
        DynamicScope.withValue deferred None (fun () -> DynamicScope.withValue active None body))
