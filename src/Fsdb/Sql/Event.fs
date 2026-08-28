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

let private tryComment (options: Fsdb.Parser.ParserOptions) (text: string) =
    match Fsdb.Parser.parseExpressionWithOptions options text with
    | Ok(Lit(VString comment)) -> Some comment
    | _ -> None

let private splitComment options (text: string) =
    match Fsdb.Parser.trySplitTopLevelKeywordWithOptions options "COMMENT" text with
    | None -> Some(text.Trim(), None)
    | Some(prefix, literal) -> tryComment options literal |> Option.map (fun comment -> prefix, Some comment)

let private stripTrailing (pattern: string) (groupName: string) (text: string) =
    let matched = Regex.Match(text, pattern, RegexOptions.IgnoreCase ||| RegexOptions.Singleline)

    if matched.Success then
        text.Substring(0, matched.Index).Trim(), Some(matched.Groups.[groupName].Value.Trim())
    else
        text.Trim(), None

let private parseCompletion (value: string) =
    if value.Equals("PRESERVE", StringComparison.OrdinalIgnoreCase) then "PRESERVE" else "NOT PRESERVE"

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
