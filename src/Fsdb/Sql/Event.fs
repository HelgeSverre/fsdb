module Fsdb.Sql.Event

open System
open System.Text.RegularExpressions
open Fsdb.Ast
open Fsdb.Value

[<RequireQualifiedAccess>]
type Status =
    | Enabled
    | Disabled
    | ReplicaSideDisabled

let statusText = function
    | Status.Enabled -> "ENABLED"
    | Status.Disabled -> "DISABLED"
    | Status.ReplicaSideDisabled -> "REPLICA_SIDE_DISABLED"

let statusOfText (value: string) =
    match value.ToUpperInvariant() with
    | "ENABLE"
    | "ENABLED" -> Status.Enabled
    | "DISABLE"
    | "DISABLED" -> Status.Disabled
    | "DISABLE ON REPLICA"
    | "DISABLE ON SLAVE"
    | "REPLICA_SIDE_DISABLED" -> Status.ReplicaSideDisabled
    | _ -> Status.Enabled

[<RequireQualifiedAccess>]
type ScheduleSpec =
    | At of expression: string
    | Every of value: string * field: string * starts: string option * ends: string option

[<RequireQualifiedAccess>]
type Timing =
    | OneTime of executeAt: DateTime
    | Recurring of
        intervalValue: string *
        intervalField: string *
        intervalAmount: float *
        intervalUnit: string *
        starts: DateTime *
        ends: DateTime option

type Creation =
    { Name: string
      IfNotExists: bool
      Schedule: string
      OnCompletion: string
      Status: Status
      Comment: string
      Body: string
      Definer: string option }

type Alteration =
    { Name: string
      Schedule: string option
      OnCompletion: string option
      RenameTo: string option
      Status: Status option
      Comment: string option
      Body: string option
      Definer: string option }

type Command =
    | Create of Creation
    | Alter of Alteration
    | Drop of name: string * ifExists: bool

let private definerPattern =
    "(?:CURRENT_USER(?:\\(\\))?|(?:'[^']*'|`[^`]*`|[A-Za-z0-9_$.-]+)(?:\\s*@\\s*(?:'[^']*'|`[^`]*`|[A-Za-z0-9_$.:/%-]+))?)"

let private createHead =
    Regex(
        "^\\s*CREATE\\s+(?:DEFINER\\s*=\\s*(?<definer>"
        + definerPattern
        + ")\\s+)?EVENT\\s+(?<ifNotExists>IF\\s+NOT\\s+EXISTS\\s+)?(?<name>\\S+)\\s+ON\\s+SCHEDULE\\s+(?<tail>.+)$",
        RegexOptions.IgnoreCase ||| RegexOptions.Singleline
    )

let private alterHead =
    Regex(
        "^\\s*ALTER\\s+(?:DEFINER\\s*=\\s*(?<definer>"
        + definerPattern
        + ")\\s+)?EVENT\\s+(?<name>\\S+)(?<tail>.*)$",
        RegexOptions.IgnoreCase ||| RegexOptions.Singleline
    )

let private dropStatement =
    Regex(@"^\s*DROP\s+EVENT\s+(?<ifExists>IF\s+EXISTS\s+)?(?<name>\S+)\s*$", RegexOptions.IgnoreCase)

let private recurringSchedule =
    Regex(
        @"^\s*EVERY\s+(?<value>.+?)\s+(?<field>YEAR_MONTH|DAY_HOUR|DAY_MINUTE|DAY_SECOND|HOUR_MINUTE|HOUR_SECOND|MINUTE_SECOND|SECOND_MICROSECOND|MINUTE_MICROSECOND|HOUR_MICROSECOND|DAY_MICROSECOND|MICROSECOND|YEAR|QUARTER|MONTH|WEEK|DAY|HOUR|MINUTE|SECOND)(?<tail>\s.*)?$",
        RegexOptions.IgnoreCase ||| RegexOptions.Singleline
    )

let private tryComment (options: Fsdb.Parser.ParserOptions) (text: string) =
    match Fsdb.Parser.parseExpressionWithOptions options text with
    | Ok(Lit(VString comment)) -> Some comment
    | _ -> None

let private splitComment options (text: string) =
    match Fsdb.Parser.trySplitTopLevelKeywordWithOptions options "COMMENT" text with
    | None -> Some(text.Trim(), None)
    | Some(prefix, literal) -> tryComment options literal |> Option.map (fun comment -> prefix, Some comment)

let private stripTrailing (pattern: string) (groupName: string) (text: string) =
    let matched =
        Regex.Match(
            text,
            pattern,
            RegexOptions.IgnoreCase ||| RegexOptions.Singleline ||| RegexOptions.NonBacktracking,
            Fsdb.Limits.regexpMatchTimeout
        )

    if matched.Success then
        text.Substring(0, matched.Index).Trim(), Some(matched.Groups.[groupName].Value.Trim())
    else
        text.Trim(), None

let private parseCompletion (value: string) =
    if value.Equals("PRESERVE", StringComparison.OrdinalIgnoreCase) then "PRESERVE" else "NOT PRESERVE"

let tryParseSchedule options (schedule: string) =
    let at = Regex.Match(schedule, @"^\s*AT\s+(?<expression>.+)$", RegexOptions.IgnoreCase ||| RegexOptions.Singleline)

    if at.Success then
        Some(ScheduleSpec.At(at.Groups.["expression"].Value.Trim()))
    else
        let recurring = recurringSchedule.Match schedule

        if not recurring.Success then
            None
        else
            let tail = recurring.Groups.["tail"].Value.Trim()

            let starts, ends, validTail =
                match Fsdb.Parser.trySplitTopLevelKeywordWithOptions options "STARTS" tail with
                | Some(prefix, afterStarts) when prefix.Trim() = "" ->
                    match Fsdb.Parser.trySplitTopLevelKeywordWithOptions options "ENDS" afterStarts with
                    | Some(startExpression, endExpression) when startExpression.Trim() <> "" && endExpression.Trim() <> "" ->
                        Some(startExpression.Trim()), Some(endExpression.Trim()), true
                    | None when afterStarts.Trim() <> "" -> Some(afterStarts.Trim()), None, true
                    | _ -> None, None, false
                | Some _ -> None, None, false
                | None ->
                    match Fsdb.Parser.trySplitTopLevelKeywordWithOptions options "ENDS" tail with
                    | Some(prefix, endExpression) when prefix.Trim() = "" && endExpression.Trim() <> "" ->
                        None, Some(endExpression.Trim()), true
                    | None when tail = "" -> None, None, true
                    | _ -> None, None, false

            if validTail then
                Some(
                    ScheduleSpec.Every(
                        recurring.Groups.["value"].Value.Trim(),
                        recurring.Groups.["field"].Value.ToUpperInvariant(),
                        starts,
                        ends
                    )
                )
            else
                None

let timingFields = function
    | Timing.OneTime executeAt -> Some executeAt, None, None, None, None
    | Timing.Recurring(intervalValue, intervalField, _, _, starts, ends) ->
        None, Some intervalValue, Some intervalField, Some starts, ends

let scheduleText = function
    | Timing.OneTime executeAt -> sprintf "AT '%s'" (executeAt.ToString("yyyy-MM-dd HH:mm:ss"))
    | Timing.Recurring(intervalValue, intervalField, _, _, starts, ends) ->
        let ending =
            ends
            |> Option.map (fun value -> sprintf " ENDS '%s'" (value.ToString("yyyy-MM-dd HH:mm:ss")))
            |> Option.defaultValue ""

        sprintf
            "EVERY %s %s STARTS '%s'%s"
            intervalValue
            intervalField
            (starts.ToString("yyyy-MM-dd HH:mm:ss"))
            ending

let tryRecurringTiming
    (intervalValue: string)
    (intervalField: string)
    (starts: DateTime)
    (ends: DateTime option)
    =
    match Fsdb.Functions.tryIntervalParts intervalValue intervalField with
    | Some(amount, unit) when amount > 0.0 && not (Double.IsInfinity amount || Double.IsNaN amount) ->
        Some(Timing.Recurring(intervalValue, intervalField.ToUpperInvariant(), amount, unit, starts, ends))
    | _ -> None

let private fixedIntervalTicks (amount: float) (unit: string) =
    let ticksPerUnit =
        match unit.ToUpperInvariant() with
        | "MICROSECOND" -> Some 10.0
        | "SECOND" -> Some(float TimeSpan.TicksPerSecond)
        | "MINUTE" -> Some(float TimeSpan.TicksPerMinute)
        | "HOUR" -> Some(float TimeSpan.TicksPerHour)
        | "DAY" -> Some(float TimeSpan.TicksPerDay)
        | "WEEK" -> Some(float TimeSpan.TicksPerDay * 7.0)
        | _ -> None

    ticksPerUnit
    |> Option.bind (fun ticks ->
        let value = amount * ticks

        if Double.IsFinite value && value >= 1.0 && value <= float Int64.MaxValue then
            Some(int64 value)
        else
            None)

let private intervalMonths (amount: float) (unit: string) =
    let scale =
        match unit.ToUpperInvariant() with
        | "MONTH" -> Some 1.0
        | "QUARTER" -> Some 3.0
        | "YEAR" -> Some 12.0
        | _ -> None

    scale
    |> Option.bind (fun scale ->
        let value = amount * scale

        if Double.IsFinite value && value >= 1.0 && value <= float Int32.MaxValue && value = Math.Truncate value then
            Some(int value)
        else
            None)

let private latestOccurrence (start: DateTime) (amount: float) (unit: string) (now: DateTime) =
    if start > now then
        None
    else
        match fixedIntervalTicks amount unit with
        | Some ticks ->
            let occurrences = (now.Ticks - start.Ticks) / ticks
            Some(DateTime(start.Ticks + occurrences * ticks, start.Kind))
        | None ->
            match intervalMonths amount unit with
            | None -> None
            | Some months ->
                let elapsedMonths = (now.Year - start.Year) * 12 + now.Month - start.Month
                let occurrences = max 0 (elapsedMonths / months)
                let candidate = start.AddMonths(occurrences * months)

                if candidate <= now then
                    Some candidate
                elif occurrences > 0 then
                    Some(start.AddMonths((occurrences - 1) * months))
                else
                    None

let dueOccurrence (now: DateTime) (lastExecuted: DateTime option) = function
    | Timing.OneTime executeAt ->
        if lastExecuted.IsNone && executeAt <= now then Some executeAt else None
    | Timing.Recurring(_, _, amount, unit, starts, ends) ->
        latestOccurrence starts amount unit now
        |> Option.filter (fun due ->
            lastExecuted |> Option.forall (fun previous -> due > previous)
            && ends |> Option.forall (fun ending -> due <= ending))

let isFinalOccurrence (due: DateTime) = function
    | Timing.OneTime _ -> true
    | (Timing.Recurring(_, _, _, _, _, Some ending) as timing) -> dueOccurrence ending (Some due) timing |> Option.isNone
    | Timing.Recurring _ -> false

let private tryCreate options validBody sql =
    match Fsdb.Parser.trySplitTopLevelKeywordWithOptions options "DO" sql with
    | Some(header, body) when body <> "" && validBody body ->
        let matched = createHead.Match header

        if not matched.Success then
            None
        else
            splitComment options matched.Groups.["tail"].Value
            |> Option.bind (fun (withoutComment, comment) ->
                let withoutStatus, status =
                    stripTrailing
                        @"(?:^|\s+)(?<status>ENABLE|DISABLE(?:\s+ON\s+(?:REPLICA|SLAVE))?)\s*$"
                        "status"
                        withoutComment

                let schedule, completion =
                    stripTrailing
                        @"(?:^|\s+)ON\s+COMPLETION\s+(?<completion>(?:NOT\s+)?PRESERVE)\s*$"
                        "completion"
                        withoutStatus

                if schedule = "" then
                    None
                else
                    Some(
                        Create
                            { Name = matched.Groups.["name"].Value
                              IfNotExists = matched.Groups.["ifNotExists"].Success
                              Schedule = schedule
                              OnCompletion = completion |> Option.map parseCompletion |> Option.defaultValue "NOT PRESERVE"
                              Status = status |> Option.map statusOfText |> Option.defaultValue Status.Enabled
                              Comment = comment |> Option.defaultValue ""
                              Body = body
                              Definer = if matched.Groups.["definer"].Success then Some matched.Groups.["definer"].Value else None }
                    ))
    | _ -> None

let private tryAlter options validBody sql =
    let header, body =
        match Fsdb.Parser.trySplitTopLevelKeywordWithOptions options "DO" sql with
        | Some(header, body) -> header, Some body
        | None -> sql, None

    let matched = alterHead.Match header

    if not matched.Success || body = Some "" || (body |> Option.exists (validBody >> not)) then
        None
    else
        splitComment options matched.Groups.["tail"].Value
        |> Option.bind (fun (withoutComment, comment) ->
            let withoutStatus, status =
                stripTrailing
                    @"(?:^|\s+)(?<status>ENABLE|DISABLE(?:\s+ON\s+(?:REPLICA|SLAVE))?)\s*$"
                    "status"
                    withoutComment

            let withoutRename, renameTo =
                stripTrailing @"(?:^|\s+)RENAME\s+TO\s+(?<rename>\S+)\s*$" "rename" withoutStatus

            let withoutCompletion, completion =
                stripTrailing
                    @"(?:^|\s+)ON\s+COMPLETION\s+(?<completion>(?:NOT\s+)?PRESERVE)\s*$"
                    "completion"
                    withoutRename

            let schedule =
                let matched = Regex.Match(withoutCompletion, @"^\s*ON\s+SCHEDULE\s+(?<schedule>.+)$", RegexOptions.IgnoreCase)
                if matched.Success then Some(matched.Groups.["schedule"].Value.Trim()) else None

            let residueValid = withoutCompletion = "" || schedule.IsSome
            let definer = if matched.Groups.["definer"].Success then Some matched.Groups.["definer"].Value else None

            if
                residueValid
                && [ schedule.IsSome; completion.IsSome; renameTo.IsSome; status.IsSome; comment.IsSome; body.IsSome; definer.IsSome ]
                   |> List.exists id
            then
                Some(
                    Alter
                        { Name = matched.Groups.["name"].Value
                          Schedule = schedule
                          OnCompletion = completion |> Option.map parseCompletion
                          RenameTo = renameTo
                          Status = status |> Option.map statusOfText
                          Comment = comment
                          Body = body
                          Definer = definer }
                )
            else
                None)

let tryCommand options validBody sql =
    match tryCreate options validBody sql, tryAlter options validBody sql with
    | Some command, _ -> Some command
    | None, Some command -> Some command
    | None, None ->
        let matched = dropStatement.Match sql

        if matched.Success then
            Some(Drop(matched.Groups.["name"].Value, matched.Groups.["ifExists"].Success))
        else
            None
