/// Opt-in durability (`--data-dir`): WAL + snapshot, replay on startup.
/// `Db.withDataDir` is the door in: `load` rebuilds a
/// `Store` from whatever's on disk, `attach` subscribes it (via
/// `Storage.Store.OnCommit`) to keep writing.
///
/// Row-level events (`RowsInserted`/`RowsUpdated`/`RowsDeleted`) already
/// carry physically-evaluated `Value[]`s — see `Storage.CommitEvent` — so
/// replaying them is "write exactly these values back", never "re-run an
/// expression" (the whole point: `INSERT ... VALUES (NOW(), UUID())`
/// replays to the *same* row, not a freshly-evaluated one). DDL
/// (`SchemaChanged`) carries the parsed `Ast.Statement`; this module hand-
/// rolls its own compact JSON encoding for the handful of DDL shapes it can
/// contain (there's no `System.Text.Json` F#-union support without a NuGet
/// package this project doesn't take a dependency on — see `Fsdb.fsproj`)
/// and replays it by calling `Storage`'s own DDL functions directly
/// (`Executor.execute` isn't reachable — `Executor.fs` compiles *after*
/// this file).
module Fsdb.Persistence

open System
open System.IO
open System.Runtime.InteropServices
open System.Text.Json.Nodes
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Storage

let private walFileName = "wal.jsonl"
let private snapshotFileName = "snapshot.fsdb"

/// `PosixSignalRegistration.Create` returns a disposable that unregisters
/// its handler when finalized — `attach`'s SIGTERM/SIGINT registrations
/// have to stay reachable for the process's whole lifetime, or the first GC
/// silently stops the shutdown snapshot from firing. See `attach`.
let private shutdownRegistrations = ResizeArray<IDisposable>()

/// Once the WAL crosses this many bytes, or this many appended entries,
/// whichever comes first, `attach`'s subscriber snapshots the whole catalog
/// and truncates it — keeps startup replay bounded instead of an
/// ever-growing WAL. ponytail: fixed constants, not a knob; promote to a
/// `Db.withDataDir` parameter if a real deployment ever needs a different
/// rotation size.
let private walRotateBytes = 64L * 1024L * 1024L
let private walRotateEntries = 100_000

// ---------------------------------------------------------------------
// JSON leaves: strings/bools/ints/lists, plus a `"case"`-tagged object for
// every DU used here — `decodeXxx` is each `encodeXxx`'s exact inverse.
// ---------------------------------------------------------------------

let private str (s: string) : JsonNode = JsonValue.Create s
let private boolNode (b: bool) : JsonNode = JsonValue.Create b
let private intNode (i: int) : JsonNode = JsonValue.Create i
let private i64Node (i: int64) : JsonNode = JsonValue.Create i
let private arr (nodes: JsonNode list) : JsonNode = JsonArray(nodes |> Array.ofList)
let private strArr (xs: string list) : JsonNode = arr (xs |> List.map str)
let private strListOf (node: JsonNode) : string list = node.AsArray() |> Seq.map (fun n -> n.GetValue<string>()) |> List.ofSeq
let private optStr (o: JsonNode) : string option = match o with null -> None | v -> Some(v.GetValue<string>())
let private strOptNode (s: string option) : JsonNode = s |> Option.map str |> Option.defaultValue null

let private caseObj (name: string) (fields: (string * JsonNode) list) : JsonNode =
    let o = JsonObject()
    o.["case"] <- str name
    fields |> List.iter (fun (k, v) -> o.[k] <- v)
    o

let private caseName (node: JsonNode) : string * JsonObject =
    let o = node.AsObject()
    o.["case"].GetValue<string>(), o

let private encodeRow (row: Value[]) : JsonNode = arr (row |> Array.toList |> List.map (toWire >> str))
let private decodeRow (node: JsonNode) : Value[] = node.AsArray() |> Seq.map (fun n -> ofWire (n.GetValue<string>())) |> Array.ofSeq
let private encodeRows (rows: Value[] list) : JsonNode = arr (rows |> List.map encodeRow)
let private decodeRows (node: JsonNode) : Value[] list = node.AsArray() |> Seq.map decodeRow |> List.ofSeq

// ---------------------------------------------------------------------
// `Ast.ColumnType`
// ---------------------------------------------------------------------

let private encodeColumnType (t: ColumnType) : JsonNode =
    match t with
    | TTinyInt u -> caseObj "TTinyInt" [ "u", boolNode u ]
    | TSmallInt u -> caseObj "TSmallInt" [ "u", boolNode u ]
    | TMediumInt u -> caseObj "TMediumInt" [ "u", boolNode u ]
    | TInt u -> caseObj "TInt" [ "u", boolNode u ]
    | TBigInt u -> caseObj "TBigInt" [ "u", boolNode u ]
    | TChar l -> caseObj "TChar" [ "l", intNode l ]
    | TVarchar l -> caseObj "TVarchar" [ "l", intNode l ]
    | TTinyText -> caseObj "TTinyText" []
    | TText -> caseObj "TText" []
    | TMediumText -> caseObj "TMediumText" []
    | TLongText -> caseObj "TLongText" []
    | TBinary l -> caseObj "TBinary" [ "l", intNode l ]
    | TVarBinary l -> caseObj "TVarBinary" [ "l", intNode l ]
    | TTinyBlob -> caseObj "TTinyBlob" []
    | TBlob -> caseObj "TBlob" []
    | TMediumBlob -> caseObj "TMediumBlob" []
    | TLongBlob -> caseObj "TLongBlob" []
    | TEnum values -> caseObj "TEnum" [ "values", strArr values ]
    | TSet values -> caseObj "TSet" [ "values", strArr values ]
    | TDecimal(p, s) -> caseObj "TDecimal" [ "p", intNode p; "s", intNode s ]
    | TDouble -> caseObj "TDouble" []
    | TFloat -> caseObj "TFloat" []
    | TDate -> caseObj "TDate" []
    | TDateTime -> caseObj "TDateTime" []
    | TTimestamp -> caseObj "TTimestamp" []
    | TTime -> caseObj "TTime" []
    | TYear -> caseObj "TYear" []
    | TJson -> caseObj "TJson" []

let private decodeColumnType (node: JsonNode) : ColumnType =
    let case, o = caseName node
    let f (k: string) = o.[k]

    match case with
    | "TTinyInt" -> TTinyInt(f("u").GetValue<bool>())
    | "TSmallInt" -> TSmallInt(f("u").GetValue<bool>())
    | "TMediumInt" -> TMediumInt(f("u").GetValue<bool>())
    | "TInt" -> TInt(f("u").GetValue<bool>())
    | "TBigInt" -> TBigInt(f("u").GetValue<bool>())
    | "TChar" -> TChar(f("l").GetValue<int>())
    | "TVarchar" -> TVarchar(f("l").GetValue<int>())
    | "TTinyText" -> TTinyText
    | "TText" -> TText
    | "TMediumText" -> TMediumText
    | "TLongText" -> TLongText
    | "TBinary" -> TBinary(f("l").GetValue<int>())
    | "TVarBinary" -> TVarBinary(f("l").GetValue<int>())
    | "TTinyBlob" -> TTinyBlob
    | "TBlob" -> TBlob
    | "TMediumBlob" -> TMediumBlob
    | "TLongBlob" -> TLongBlob
    | "TEnum" -> TEnum(strListOf (f "values"))
    | "TSet" -> TSet(strListOf (f "values"))
    | "TDecimal" -> TDecimal(f("p").GetValue<int>(), f("s").GetValue<int>())
    | "TDouble" -> TDouble
    | "TFloat" -> TFloat
    | "TDate" -> TDate
    | "TDateTime" -> TDateTime
    | "TTimestamp" -> TTimestamp
    | "TTime" -> TTime
    | "TYear" -> TYear
    | "TJson" -> TJson
    | tag -> failwithf "Persistence: unknown ColumnType case '%s' in WAL/snapshot" tag

// ---------------------------------------------------------------------
// `Ast.Expr` — the only place one needs to survive the WAL/snapshot at all
// is `ColumnDef.Generated` (`GENERATED ALWAYS AS (expr)`), and MySQL itself
// rejects a subquery there, so `InSubquery`/`Exists`/`Subquery` fail loudly
// rather than needing a `SelectStmt` encoder too — a real migration can't
// produce one here to lose in the first place.
// ---------------------------------------------------------------------

let private encodeOp (op: Op) : JsonNode =
    str (
        match op with
        | And -> "And"
        | Or -> "Or"
        | Eq -> "Eq"
        | Neq -> "Neq"
        | Lt -> "Lt"
        | Lte -> "Lte"
        | Gt -> "Gt"
        | Gte -> "Gte"
        | Add -> "Add"
        | Sub -> "Sub"
        | Mul -> "Mul"
        | Div -> "Div"
        | IntDiv -> "IntDiv"
        | NullSafeEq -> "NullSafeEq"
    )

let private decodeOp (node: JsonNode) : Op =
    match node.GetValue<string>() with
    | "And" -> And
    | "Or" -> Or
    | "Eq" -> Eq
    | "Neq" -> Neq
    | "Lt" -> Lt
    | "Lte" -> Lte
    | "Gt" -> Gt
    | "Gte" -> Gte
    | "Add" -> Add
    | "Sub" -> Sub
    | "Mul" -> Mul
    | "Div" -> Div
    | "IntDiv" -> IntDiv
    | "NullSafeEq" -> NullSafeEq
    | tag -> failwithf "Persistence: unknown Op case '%s' in WAL/snapshot" tag

let private encodeDirection (d: Direction) : JsonNode = str (match d with Asc -> "Asc" | Desc -> "Desc")

let private decodeDirection (node: JsonNode) : Direction =
    match node.GetValue<string>() with
    | "Asc" -> Asc
    | "Desc" -> Desc
    | tag -> failwithf "Persistence: unknown Direction case '%s' in WAL/snapshot" tag

let rec private encodeExpr (expr: Expr) : JsonNode =
    match expr with
    | Lit v -> caseObj "Lit" [ "v", str (toWire v) ]
    | Col name -> caseObj "Col" [ "name", str name ]
    | QualifiedCol(t, c) -> caseObj "QualifiedCol" [ "table", str t; "column", str c ]
    | BinOp(op, a, b) -> caseObj "BinOp" [ "op", encodeOp op; "a", encodeExpr a; "b", encodeExpr b ]
    | Not e -> caseObj "Not" [ "e", encodeExpr e ]
    | IsNull e -> caseObj "IsNull" [ "e", encodeExpr e ]
    | IsNotNull e -> caseObj "IsNotNull" [ "e", encodeExpr e ]
    | IsTrue e -> caseObj "IsTrue" [ "e", encodeExpr e ]
    | IsFalse e -> caseObj "IsFalse" [ "e", encodeExpr e ]
    | Like(e, p, cs, esc) ->
        caseObj
            "Like"
            [ "e", encodeExpr e
              "p", encodeExpr p
              "cs", boolNode cs
              "esc", strOptNode (esc |> Option.map string) ]
    | Regexp(e, p) -> caseObj "Regexp" [ "e", encodeExpr e; "p", encodeExpr p ]
    | In(e, xs) -> caseObj "In" [ "e", encodeExpr e; "xs", arr (xs |> List.map encodeExpr) ]
    | Between(e, lo, hi) -> caseObj "Between" [ "e", encodeExpr e; "lo", encodeExpr lo; "hi", encodeExpr hi ]
    | FuncCall(name, args) -> caseObj "FuncCall" [ "name", str name; "args", arr (args |> List.map encodeExpr) ]
    | RowNumberOver(partitionBy, orderBy) ->
        caseObj
            "RowNumberOver"
            [ "partitionBy", arr (partitionBy |> List.map encodeExpr)
              "orderBy", arr (orderBy |> List.map (fun (e, d) -> arr [ encodeExpr e; encodeDirection d ])) ]
    | LagOver(e, offset, partitionBy, orderBy) ->
        caseObj
            "LagOver"
            [ "e", encodeExpr e
              "offset", i64Node offset
              "partitionBy", arr (partitionBy |> List.map encodeExpr)
              "orderBy", arr (orderBy |> List.map (fun (e, d) -> arr [ encodeExpr e; encodeDirection d ])) ]
    | Distinct e -> caseObj "Distinct" [ "e", encodeExpr e ]
    | OrderBy(e, d) -> caseObj "OrderBy" [ "e", encodeExpr e; "d", encodeDirection d ]
    | Cast(e, t) -> caseObj "Cast" [ "e", encodeExpr e; "t", encodeColumnType t ]
    | Star q -> caseObj "Star" [ "q", strOptNode q ]
    | Case(subject, whens, elseBranch) ->
        caseObj
            "Case"
            [ "subject", (subject |> Option.map encodeExpr |> Option.defaultValue null)
              "whens", arr (whens |> List.map (fun (c, r) -> arr [ encodeExpr c; encodeExpr r ]))
              "else", (elseBranch |> Option.map encodeExpr |> Option.defaultValue null) ]
    | InSubquery _
    | Exists _
    | Subquery _ -> failwithf "Persistence: a GENERATED column can't hold a subquery (MySQL itself rejects one there)"

let rec private decodeExpr (node: JsonNode) : Expr =
    let case, o = caseName node
    let f (k: string) = o.[k]

    match case with
    | "Lit" -> Lit(ofWire (f("v").GetValue<string>()))
    | "Col" -> Col(f("name").GetValue<string>())
    | "QualifiedCol" -> QualifiedCol(f("table").GetValue<string>(), f("column").GetValue<string>())
    | "BinOp" -> BinOp(decodeOp (f "op"), decodeExpr (f "a"), decodeExpr (f "b"))
    | "Not" -> Not(decodeExpr (f "e"))
    | "IsNull" -> IsNull(decodeExpr (f "e"))
    | "IsNotNull" -> IsNotNull(decodeExpr (f "e"))
    | "IsTrue" -> IsTrue(decodeExpr (f "e"))
    | "IsFalse" -> IsFalse(decodeExpr (f "e"))
    | "Like" -> Like(decodeExpr (f "e"), decodeExpr (f "p"), f("cs").GetValue<bool>(), optStr (f "esc") |> Option.map (fun s -> s.[0]))
    | "Regexp" -> Regexp(decodeExpr (f "e"), decodeExpr (f "p"))
    | "In" -> In(decodeExpr (f "e"), f("xs").AsArray() |> Seq.map decodeExpr |> List.ofSeq)
    | "Between" -> Between(decodeExpr (f "e"), decodeExpr (f "lo"), decodeExpr (f "hi"))
    | "FuncCall" -> FuncCall(f("name").GetValue<string>(), f("args").AsArray() |> Seq.map decodeExpr |> List.ofSeq)
    | "RowNumberOver" ->
        RowNumberOver(
            f("partitionBy").AsArray() |> Seq.map decodeExpr |> List.ofSeq,
            f("orderBy").AsArray()
            |> Seq.map (fun p ->
                let pair = p.AsArray()
                decodeExpr pair.[0], decodeDirection pair.[1])
            |> List.ofSeq
        )
    | "LagOver" ->
        LagOver(
            decodeExpr (f "e"),
            f("offset").GetValue<int64>(),
            f("partitionBy").AsArray() |> Seq.map decodeExpr |> List.ofSeq,
            f("orderBy").AsArray()
            |> Seq.map (fun p ->
                let pair = p.AsArray()
                decodeExpr pair.[0], decodeDirection pair.[1])
            |> List.ofSeq
        )
    | "Distinct" -> Distinct(decodeExpr (f "e"))
    | "OrderBy" -> OrderBy(decodeExpr (f "e"), decodeDirection (f "d"))
    | "Cast" -> Cast(decodeExpr (f "e"), decodeColumnType (f "t"))
    | "Star" -> Star(optStr (f "q"))
    | "Case" ->
        Case(
            (match f "subject" with
             | null -> None
             | s -> Some(decodeExpr s)),
            f("whens").AsArray()
            |> Seq.map (fun p ->
                let pair = p.AsArray()
                decodeExpr pair.[0], decodeExpr pair.[1])
            |> List.ofSeq,
            (match f "else" with
             | null -> None
             | e -> Some(decodeExpr e))
        )
    | tag -> failwithf "Persistence: unknown Expr case '%s' in WAL/snapshot" tag

// ---------------------------------------------------------------------
// `Ast.ColumnDefault` / `ColumnDef` / `IndexDef` / `ForeignKeyDef`
// ---------------------------------------------------------------------

let private encodeColumnDefault (d: ColumnDefault) : JsonNode =
    match d with
    | DConst v -> caseObj "DConst" [ "value", str (toWire v) ]
    | DCurrentTimestamp -> caseObj "DCurrentTimestamp" []

let private decodeColumnDefault (node: JsonNode) : ColumnDefault =
    let case, o = caseName node

    match case with
    | "DConst" -> DConst(ofWire (o.["value"].GetValue<string>()))
    | "DCurrentTimestamp" -> DCurrentTimestamp
    | tag -> failwithf "Persistence: unknown ColumnDefault case '%s' in WAL/snapshot" tag

/// `ColumnDef.Generated` (`GENERATED ALWAYS AS (expr)`) round-trips through
/// the `Expr` encoder above — without it, a generated column silently
/// stopped being computed after a restart (new rows got `NULL` where MySQL
/// computes a value; Laravel Pulse's `key_hash ... AS (unhex(md5(key)))` is
/// exactly this shape), not just the cosmetic `SHOW CREATE TABLE` gap
/// `InformationSchema.showCreateTableDDL` has.
let private encodeColumnDef (c: ColumnDef) : JsonNode =
    let o = JsonObject()
    o.["name"] <- str c.Name
    o.["type"] <- encodeColumnType c.Type
    o.["nullable"] <- boolNode c.Nullable
    o.["default"] <- (c.Default |> Option.map encodeColumnDefault |> Option.defaultValue null)
    o.["autoIncrement"] <- boolNode c.AutoIncrement
    o.["primaryKey"] <- boolNode c.PrimaryKey
    o.["unique"] <- boolNode c.Unique
    o.["generated"] <- (c.Generated |> Option.map encodeExpr |> Option.defaultValue null)
    o

let private decodeColumnDef (node: JsonNode) : ColumnDef =
    let o = node.AsObject()

    { Name = o.["name"].GetValue<string>()
      Type = decodeColumnType o.["type"]
      Nullable = o.["nullable"].GetValue<bool>()
      Default = (match o.["default"] with null -> None | d -> Some(decodeColumnDefault d))
      AutoIncrement = o.["autoIncrement"].GetValue<bool>()
      PrimaryKey = o.["primaryKey"].GetValue<bool>()
      Unique = o.["unique"].GetValue<bool>()
      Generated = (match o.["generated"] with null -> None | g -> Some(decodeExpr g)) }

let private encodeIndexDef (ix: IndexDef) : JsonNode =
    let o = JsonObject()
    o.["name"] <- str ix.Name
    o.["columns"] <- strArr ix.Columns
    o.["unique"] <- boolNode ix.Unique
    o

let private decodeIndexDef (node: JsonNode) : IndexDef =
    let o = node.AsObject()
    { Name = o.["name"].GetValue<string>(); Columns = strListOf o.["columns"]; Unique = o.["unique"].GetValue<bool>() }

let private encodeForeignKeyDef (fk: ForeignKeyDef) : JsonNode =
    let o = JsonObject()
    o.["name"] <- str fk.Name
    o.["columns"] <- strArr fk.Columns
    o.["refTable"] <- str fk.RefTable
    o.["refColumns"] <- strArr fk.RefColumns
    o.["onDelete"] <- strOptNode fk.OnDelete
    o.["onUpdate"] <- strOptNode fk.OnUpdate
    o

let private decodeForeignKeyDef (node: JsonNode) : ForeignKeyDef =
    let o = node.AsObject()

    { Name = o.["name"].GetValue<string>()
      Columns = strListOf o.["columns"]
      RefTable = o.["refTable"].GetValue<string>()
      RefColumns = strListOf o.["refColumns"]
      OnDelete = optStr o.["onDelete"]
      OnUpdate = optStr o.["onUpdate"] }

// ---------------------------------------------------------------------
// `Ast.AlterAction` / `Ast.Statement` — only the DDL shapes `SchemaChanged`
// ever wraps (see `Storage`'s every `emit (Some(SchemaChanged ...)))` call);
// any other `Statement` case reaching here would be a `Storage` bug, not a
// WAL-format one, so it's a hard `failwithf` rather than a silent skip.
// ---------------------------------------------------------------------

let private encodeColumnPosition (p: ColumnPosition) : JsonNode =
    match p with
    | PositionDefault -> caseObj "PositionDefault" []
    | PositionFirst -> caseObj "PositionFirst" []
    | PositionAfter column -> caseObj "PositionAfter" [ "column", str column ]

let private decodeColumnPosition (node: JsonNode) : ColumnPosition =
    let case, o = caseName node

    match case with
    | "PositionDefault" -> PositionDefault
    | "PositionFirst" -> PositionFirst
    | "PositionAfter" -> PositionAfter(o.["column"].GetValue<string>())
    | tag -> failwithf "Persistence: unknown ColumnPosition case '%s' in WAL/snapshot" tag

let private encodeAlterAction (a: AlterAction) : JsonNode =
    match a with
    | AddColumn(c, position) -> caseObj "AddColumn" [ "column", encodeColumnDef c; "position", encodeColumnPosition position ]
    | DropColumn name -> caseObj "DropColumn" [ "name", str name ]
    | ModifyColumn(c, position) -> caseObj "ModifyColumn" [ "column", encodeColumnDef c; "position", encodeColumnPosition position ]
    | ChangeColumn(oldName, c, position) ->
        caseObj "ChangeColumn" [ "oldName", str oldName; "column", encodeColumnDef c; "position", encodeColumnPosition position ]
    | RenameTo name -> caseObj "RenameTo" [ "name", str name ]
    | RenameColumnTo(oldName, newName) -> caseObj "RenameColumnTo" [ "oldName", str oldName; "newName", str newName ]
    | AddIndex ix -> caseObj "AddIndex" [ "index", encodeIndexDef ix ]
    | DropIndexAction name -> caseObj "DropIndexAction" [ "name", str name ]
    | AddForeignKey fk -> caseObj "AddForeignKey" [ "fk", encodeForeignKeyDef fk ]
    | DropForeignKey name -> caseObj "DropForeignKey" [ "name", str name ]
    | AddPrimaryKey columns -> caseObj "AddPrimaryKey" [ "columns", strArr columns ]

let private decodeAlterAction (node: JsonNode) : AlterAction =
    let case, o = caseName node

    match case with
    | "AddColumn" -> AddColumn(decodeColumnDef o.["column"], decodeColumnPosition o.["position"])
    | "DropColumn" -> DropColumn(o.["name"].GetValue<string>())
    | "ModifyColumn" -> ModifyColumn(decodeColumnDef o.["column"], decodeColumnPosition o.["position"])
    | "ChangeColumn" -> ChangeColumn(o.["oldName"].GetValue<string>(), decodeColumnDef o.["column"], decodeColumnPosition o.["position"])
    | "RenameTo" -> RenameTo(o.["name"].GetValue<string>())
    | "RenameColumnTo" -> RenameColumnTo(o.["oldName"].GetValue<string>(), o.["newName"].GetValue<string>())
    | "AddIndex" -> AddIndex(decodeIndexDef o.["index"])
    | "DropIndexAction" -> DropIndexAction(o.["name"].GetValue<string>())
    | "AddForeignKey" -> AddForeignKey(decodeForeignKeyDef o.["fk"])
    | "DropForeignKey" -> DropForeignKey(o.["name"].GetValue<string>())
    | "AddPrimaryKey" -> AddPrimaryKey(strListOf o.["columns"])
    | tag -> failwithf "Persistence: unknown AlterAction case '%s' in WAL/snapshot" tag

let private encodeStatement (s: Statement) : JsonNode =
    match s with
    | CreateDatabase(name, ifNotExists) -> caseObj "CreateDatabase" [ "name", str name; "ifNotExists", boolNode ifNotExists ]
    | DropDatabase(name, ifExists) -> caseObj "DropDatabase" [ "name", str name; "ifExists", boolNode ifExists ]
    | CreateTable(name, columns, indexes, fks, ifNotExists) ->
        caseObj
            "CreateTable"
            [ "name", str name
              "columns", arr (columns |> List.map encodeColumnDef)
              "indexes", arr (indexes |> List.map encodeIndexDef)
              "foreignKeys", arr (fks |> List.map encodeForeignKeyDef)
              "ifNotExists", boolNode ifNotExists ]
    | DropTable(names, ifExists) -> caseObj "DropTable" [ "names", strArr names; "ifExists", boolNode ifExists ]
    | AlterTable(table, actions) -> caseObj "AlterTable" [ "table", str table; "actions", arr (actions |> List.map encodeAlterAction) ]
    | RenameTable pairs -> caseObj "RenameTable" [ "pairs", arr (pairs |> List.map (fun (a, b) -> arr [ str a; str b ])) ]
    | CreateIndex(name, table, columns, unique) ->
        caseObj "CreateIndex" [ "name", str name; "table", str table; "columns", strArr columns; "unique", boolNode unique ]
    | DropIndexStmt(name, table) -> caseObj "DropIndexStmt" [ "name", str name; "table", str table ]
    | Truncate table -> caseObj "Truncate" [ "table", str table ]
    | other -> failwithf "Persistence: %A isn't a DDL statement SchemaChanged should ever carry" other

let private decodeStatement (node: JsonNode) : Statement =
    let case, o = caseName node

    match case with
    | "CreateDatabase" -> CreateDatabase(o.["name"].GetValue<string>(), o.["ifNotExists"].GetValue<bool>())
    | "DropDatabase" -> DropDatabase(o.["name"].GetValue<string>(), o.["ifExists"].GetValue<bool>())
    | "CreateTable" ->
        CreateTable(
            o.["name"].GetValue<string>(),
            o.["columns"].AsArray() |> Seq.map decodeColumnDef |> List.ofSeq,
            o.["indexes"].AsArray() |> Seq.map decodeIndexDef |> List.ofSeq,
            o.["foreignKeys"].AsArray() |> Seq.map decodeForeignKeyDef |> List.ofSeq,
            o.["ifNotExists"].GetValue<bool>()
        )
    | "DropTable" -> DropTable(strListOf o.["names"], o.["ifExists"].GetValue<bool>())
    | "AlterTable" -> AlterTable(o.["table"].GetValue<string>(), o.["actions"].AsArray() |> Seq.map decodeAlterAction |> List.ofSeq)
    | "RenameTable" ->
        RenameTable(
            o.["pairs"].AsArray()
            |> Seq.map (fun p ->
                let pair = p.AsArray()
                pair.[0].GetValue<string>(), pair.[1].GetValue<string>())
            |> List.ofSeq
        )
    | "CreateIndex" ->
        CreateIndex(o.["name"].GetValue<string>(), o.["table"].GetValue<string>(), strListOf o.["columns"], o.["unique"].GetValue<bool>())
    | "DropIndexStmt" -> DropIndexStmt(o.["name"].GetValue<string>(), o.["table"].GetValue<string>())
    | "Truncate" -> Truncate(o.["table"].GetValue<string>())
    | tag -> failwithf "Persistence: unknown Statement case '%s' in WAL/snapshot" tag

// ---------------------------------------------------------------------
// `Storage.CommitEvent` — one WAL line each.
// ---------------------------------------------------------------------

let rec private encodeEvent (event: CommitEvent) : JsonNode =
    match event with
    | RowsInserted(db, table, rows) -> caseObj "RowsInserted" [ "db", str db; "table", str table; "rows", encodeRows rows ]
    | RowsUpdated(db, table, changes) ->
        caseObj
            "RowsUpdated"
            [ "db", str db
              "table", str table
              "changes", arr (changes |> List.map (fun (before, after) -> arr [ encodeRow before; encodeRow after ])) ]
    | RowsDeleted(db, table, rows) -> caseObj "RowsDeleted" [ "db", str db; "table", str table; "rows", encodeRows rows ]
    | SchemaChanged(db, stmt) -> caseObj "SchemaChanged" [ "db", str db; "stmt", encodeStatement stmt ]
    | TransactionCommitted events -> caseObj "TransactionCommitted" [ "events", arr (events |> List.map encodeEvent) ]

let rec private decodeEvent (node: JsonNode) : CommitEvent =
    let case, o = caseName node

    match case with
    | "RowsInserted" -> RowsInserted(o.["db"].GetValue<string>(), o.["table"].GetValue<string>(), decodeRows o.["rows"])
    | "RowsUpdated" ->
        let changes =
            o.["changes"].AsArray()
            |> Seq.map (fun pair ->
                let p = pair.AsArray()
                decodeRow p.[0], decodeRow p.[1])
            |> List.ofSeq

        RowsUpdated(o.["db"].GetValue<string>(), o.["table"].GetValue<string>(), changes)
    | "RowsDeleted" -> RowsDeleted(o.["db"].GetValue<string>(), o.["table"].GetValue<string>(), decodeRows o.["rows"])
    | "SchemaChanged" -> SchemaChanged(o.["db"].GetValue<string>(), decodeStatement o.["stmt"])
    | "TransactionCommitted" -> TransactionCommitted(o.["events"].AsArray() |> Seq.map decodeEvent |> List.ofSeq)
    | tag -> failwithf "Persistence: unknown CommitEvent case '%s' in WAL" tag

// ---------------------------------------------------------------------
// Replay: applies a decoded `CommitEvent` to a live `Store` via `Storage`'s
// own public write functions — row events write the exact physical values
// back (see the module doc), DDL events redo the same `Storage` call the
// original statement made.
// ---------------------------------------------------------------------

let private warn (context: string) (result: Result<'a, StorageError>) : unit =
    match result with
    | Ok _ -> ()
    | Error e -> Log.diagnostic "fsdb: WAL replay warning (%s): %A" context e

let private applyDdl (store: Store) (db: string) (stmt: Statement) : unit =
    match stmt with
    | CreateDatabase(name, _) -> warn "CreateDatabase" (createDatabase store name)
    | DropDatabase(name, _) -> warn "DropDatabase" (dropDatabase store name)
    | CreateTable(name, columns, indexes, fks, _) -> warn "CreateTable" (createTable store db name columns indexes fks)
    | DropTable(names, _) -> names |> List.iter (fun n -> warn "DropTable" (dropTable store db n))
    | AlterTable(table, actions) -> warn "AlterTable" (alterTable store db table actions)
    | RenameTable pairs -> pairs |> List.iter (fun (oldName, newName) -> warn "RenameTable" (renameTable store db oldName newName))
    | CreateIndex(name, table, columns, unique) ->
        warn "CreateIndex" (alterTable store db table [ AddIndex { Name = name; Columns = columns; Unique = unique } ])
    | DropIndexStmt(name, table) -> warn "DropIndexStmt" (alterTable store db table [ DropIndexAction name ])
    | Truncate table -> warn "Truncate" (truncate store db table)
    | other -> Log.diagnostic "fsdb: WAL replay warning (SchemaChanged): unexpected statement %A" other

/// Applies `changes` — `(before, after)` pairs in the same ascending
/// original-row order `Storage.updateRows` emitted them, one entry per
/// physically distinct row it touched — to `rows` in a single forward pass:
/// walk both lists together, consuming the next change only once it's due
/// (its `before` matches the row in hand). Never re-scans from the top of
/// `rows` per change, so a change's `after` value can't be picked back up
/// as a later change's `before` — the cascade a naive "find any row equal
/// to `before`, replace it" replay hits on `UPDATE t SET n = n + 1` (every
/// row's `after` equals the next row's `before`). Any two rows the pass
/// can't tell apart are byte-identical, so it never matters which one
/// consumes which change.
let private applyRowChanges (changes: (Value[] * Value[]) list) (rows: Value[] list) : Value[] list =
    let rec loop acc rows changes =
        match rows, changes with
        | [], _ -> List.rev acc
        | row :: restRows, (before, after) :: restChanges when row = before -> loop (after :: acc) restRows restChanges
        | row :: restRows, _ -> loop (row :: acc) restRows changes

    loop [] rows changes

/// As `applyRowChanges`, for `RowsDeleted`'s logged rows: drops the next
/// not-yet-consumed match for each logged row, in order — fixes replaying a
/// partial delete over duplicate-valued rows (`DELETE ... LIMIT 1` over two
/// identical rows) wiping every row equal to the target instead of just the
/// one that was actually removed.
let private applyRowDeletes (targets: Value[] list) (rows: Value[] list) : Value[] list =
    let rec loop acc rows targets =
        match rows, targets with
        | [], _ -> List.rev acc
        | row :: restRows, target :: restTargets when row = target -> loop acc restRows restTargets
        | row :: restRows, _ -> loop (row :: acc) restRows targets

    loop [] rows targets

/// Rewrites `dbName.tableName`'s `Rows` in `store.Catalog` directly with
/// `f`, bypassing `Storage.updateRows`/`deleteRows` entirely. Replay is a
/// physical log of rows that already passed every check once, at commit
/// time — routing it back through those checked write paths is what causes
/// the cascade/over-delete bugs `applyRowChanges`/`applyRowDeletes` fix (a
/// value-equality predicate scanning the *whole* table can't express "this
/// one physical row"), and would also re-run FK/unique validation that a
/// `SET FOREIGN_KEY_CHECKS = 0` write may have deliberately skipped.
///
/// Deliberately leaves `UniqueIndex` stale rather than calling
/// `reindexTable` here — nothing reads it mid-replay (FK checks are off,
/// see `load`), so paying its full-table-rescan cost once per *event*
/// would be pure waste for a table with many updates/deletes in the WAL.
/// `replayWal`'s caller reindexes every table exactly once, after the last
/// event has been applied.
let private mapTableRows (store: Store) (dbName: string) (tableName: string) (f: Value[] list -> Value[] list) : unit =
    replaceTablesForReplay store dbName tableName f (Log.diagnostic "fsdb: WAL replay warning: %s")

let rec private applyEvent (store: Store) (event: CommitEvent) : unit =
    match event with
    | RowsInserted(db, table, rows) ->
        if not rows.IsEmpty then
            warn "RowsInserted" (insertRows store db table None (rows |> List.map List.ofArray) |> Result.map ignore)
    | RowsUpdated(db, table, changes) -> mapTableRows store db table (applyRowChanges changes)
    | RowsDeleted(db, table, rows) -> mapTableRows store db table (applyRowDeletes rows)
    | SchemaChanged(db, stmt) -> applyDdl store db stmt
    | TransactionCommitted events -> events |> List.iter (applyEvent store)

/// Replays every complete line in `walPath` into `store`, returning the
/// byte offset just past the last successfully applied line. A torn final
/// line (a `kill -9` mid-write) is expected, not corruption to panic over —
/// everything before it already committed durably — so replay stops there
/// rather than guessing at what the rest of the line might have meant;
/// `load` truncates the WAL back to the returned offset so the *next*
/// append glues onto a clean record boundary instead of the torn bytes
/// (otherwise every write from then on decodes into one ever-growing
/// corrupt line and is lost again at the next restart).
let private replayWal (store: Store) (walPath: string) : int64 =
    if not (File.Exists walPath) then
        0L
    else
        let mutable offset = 0L
        let mutable stopped = false

        for line in File.ReadAllLines walPath do
            if not stopped then
                let lineBytes = int64 (Text.Encoding.UTF8.GetByteCount line) + 1L

                if String.IsNullOrWhiteSpace line then
                    offset <- offset + lineBytes
                else
                    try
                        applyEvent store (decodeEvent (JsonNode.Parse line))
                        offset <- offset + lineBytes
                    with ex ->
                        Log.diagnostic "fsdb: WAL replay stopped at a truncated/corrupt line (%s): %s" walPath ex.Message
                        stopped <- true

        offset

// ---------------------------------------------------------------------
// Snapshot: the whole catalog, JSON, written atomically (tmp + rename).
// ---------------------------------------------------------------------

let private encodeTable (t: Table) : JsonNode =
    let o = JsonObject()
    o.["originalName"] <- str t.OriginalName
    o.["columns"] <- arr (t.Columns |> List.map encodeColumnDef)
    o.["indexes"] <- arr (t.Indexes |> List.map encodeIndexDef)
    o.["foreignKeys"] <- arr (t.ForeignKeys |> List.map encodeForeignKeyDef)
    o.["nextAutoId"] <- i64Node t.NextAutoId
    o.["rows"] <- encodeRows t.Rows
    o

let private decodeTable (node: JsonNode) : Table =
    let o = node.AsObject()

    reindexTable
        { OriginalName = o.["originalName"].GetValue<string>()
          Columns = o.["columns"].AsArray() |> Seq.map decodeColumnDef |> List.ofSeq
          Indexes = o.["indexes"].AsArray() |> Seq.map decodeIndexDef |> List.ofSeq
          ForeignKeys = o.["foreignKeys"].AsArray() |> Seq.map decodeForeignKeyDef |> List.ofSeq
          Rows = decodeRows o.["rows"]
          NextAutoId = o.["nextAutoId"].GetValue<int64>()
          UniqueIndex = Map.empty }

let private encodeCatalog (catalog: Catalog) : JsonNode =
    let databases = JsonObject()

    catalog
    |> Map.iter (fun dbName db ->
        let tables = JsonObject()
        db |> Map.iter (fun tableKey table -> tables.[tableKey] <- encodeTable table)
        databases.[dbName] <- tables)

    let root = JsonObject()
    root.["databases"] <- databases
    root

let private decodeCatalog (node: JsonNode) : Catalog =
    node.AsObject().["databases"].AsObject()
    |> Seq.map (fun dbEntry ->
        let db = dbEntry.Value.AsObject() |> Seq.map (fun t -> t.Key, decodeTable t.Value) |> Map.ofSeq
        dbEntry.Key, db)
    |> Map.ofSeq

/// Snapshots `store`'s whole catalog to `dataDir/snapshot.fsdb` and
/// truncates the WAL back to empty. Safe to call any time — holds
/// `store.Lock` for the duration, same as every other write.
///
/// Ordered (and fsynced) so a crash at any point still leaves `load` a
/// consistent state to recover from: write the *whole* catalog to
/// `snapshot.fsdb.new` through a `FileStream` and `Flush true` (an fsync,
/// matching `attach`'s WAL writes — `File.WriteAllText` never syncs, so the
/// old tmp-file dance could lose the snapshot to a power loss right after
/// the WAL truncation it's supposed to be a backup for), *then* truncate
/// the WAL, *then* rename `.new` into place. A crash between the fsync and
/// the rename leaves both `.new` (complete) and the old `snapshot.fsdb`
/// (stale but harmless) plus a truncated WAL on disk; `load` prefers `.new`
/// when it's there and parses cleanly. A crash *before* the fsync leaves an
/// incomplete/absent `.new` and the WAL untouched, so `load` falls back to
/// the old snapshot + full WAL replay — nothing lost either way.
let snapshotNow (dataDir: string) (store: Store) : unit =
    lock store.Lock (fun () ->
        Directory.CreateDirectory dataDir |> ignore
        let finalPath = Path.Combine(dataDir, snapshotFileName)
        let newPath = finalPath + ".new"

        (use s = new FileStream(newPath, FileMode.Create, FileAccess.Write)
         let bytes = Text.Encoding.UTF8.GetBytes((encodeCatalog store.Catalog).ToJsonString())
         s.Write(bytes, 0, bytes.Length)
         s.Flush true)

        File.WriteAllText(Path.Combine(dataDir, walFileName), "")
        File.Move(newPath, finalPath, true))

/// Loads durable state from `dataDir` into a fresh `Store`: the snapshot if
/// one exists, then any WAL entries written after it. Call once at startup,
/// before `attach` subscribes the result to further writes. An empty/
/// nonexistent `dataDir` loads the same empty `Store` a plain `Storage.create
/// ()` would.
let load (dataDir: string) : Store =
    let store = Storage.create ()
    Directory.CreateDirectory dataDir |> ignore
    let snapshotPath = Path.Combine(dataDir, snapshotFileName)
    let newPath = snapshotPath + ".new"
    let walPath = Path.Combine(dataDir, walFileName)

    // See `snapshotNow`: a fully-written `.new` is a superset of whatever's
    // in the WAL (it's fsynced *before* the WAL is truncated), so prefer it
    // and skip the WAL entirely rather than double-applying what it already
    // has. A `.new` that fails to parse means the crash landed mid-write,
    // before the fsync — it's garbage, not authoritative; fall through to
    // the untouched old snapshot + full WAL instead.
    let loadedFromNew =
        File.Exists newPath
        && (try
                setCatalog store (decodeCatalog (JsonNode.Parse(File.ReadAllText newPath)))
                true
            with _ ->
                false)

    if loadedFromNew then
        File.Move(newPath, snapshotPath, true)
        File.WriteAllText(walPath, "")
    else
        if File.Exists snapshotPath then
            setCatalog store (decodeCatalog (JsonNode.Parse(File.ReadAllText snapshotPath)))

        // The WAL holds rows that already passed every check once, at
        // commit time — re-validating foreign keys on replay only risks
        // dropping one written under `SET FOREIGN_KEY_CHECKS = 0` (wired up
        // at `QueryHandler.fs`, used by Laravel migrations/seeders), since
        // event order already preserves whatever check *was* enforced.
        store.ForeignKeyChecks <- false
        let goodOffset = replayWal store walPath
        store.ForeignKeyChecks <- true

        // `mapTableRows` (RowsUpdated/RowsDeleted) left `UniqueIndex` stale
        // per-table, not per-event — one rescan per table here instead of
        // one per replayed row-change event.
        reindexAllForReplay store

        // A torn final line (`kill -9` mid-append) must not poison the WAL
        // forever — see `replayWal`'s doc comment.
        if File.Exists walPath && FileInfo(walPath).Length <> goodOffset then
            use fs = new FileStream(walPath, FileMode.Open, FileAccess.Write)
            fs.SetLength goodOffset

    store

/// Subscribes `store` to `Storage.Store.OnCommit`, appending every commit as
/// one JSON line to `dataDir/wal.jsonl`, `Flush`ed (with an fsync) before
/// the write that triggered it returns — durability means fsync-before-ack.
/// Opens, writes and closes the file fresh per line rather than holding one
/// long-lived handle: `snapshotNow` (called both below, on rotation, and by
/// any other caller) truncates this same file by path, and a cached
/// `FileStream`'s internal position doesn't know that happened — its next
/// write would land at its old (now-stale) offset, zero-filling the gap in
/// between. Opening fresh every time always sees the file's true current
/// end. ponytail: one open + `fsync` per commit, no group-commit batching or
/// handle reuse; add a batching knob if write throughput ever needs it.
///
/// Auto-rotates (snapshot + WAL truncate, via `snapshotNow`) once the WAL
/// crosses `walRotateBytes`/`walRotateEntries`, and does one last rotation
/// when the process gets a SIGTERM/SIGINT so a graceful shutdown always
/// leaves a fresh snapshot behind (a `kill -9` skips this — replaying the
/// WAL from the last snapshot is what that's for).
let attach (dataDir: string) (store: Store) : unit =
    Directory.CreateDirectory dataDir |> ignore
    let walPath = Path.Combine(dataDir, walFileName)
    let entryCount = ref 0

    let appendLine (line: string) =
        let walSize =
            try
                use s = new FileStream(walPath, FileMode.Append, FileAccess.Write, FileShare.Read)
                let bytes = Text.Encoding.UTF8.GetBytes(line + "\n")
                s.Write(bytes, 0, bytes.Length)
                s.Flush true
                s.Length
            with ex ->
                // By the time `OnCommit` fires, `Storage` has already
                // applied the mutation to `store.Catalog` (see
                // `Storage.emit`) — a WAL append failing here (ENOSPC,
                // permissions, …) can't be turned into a client-visible
                // error without silently serving a row that exists only in
                // memory, invisible to the very durability mechanism whose
                // job is to keep it. Crash rather than keep serving reads
                // and writes against a catalog the WAL can no longer prove.
                Log.diagnostic "fsdb: WAL append failed, catalog and disk have diverged: %s" ex.Message
                Environment.FailFast(sprintf "fsdb: fatal WAL append failure: %s" ex.Message, ex)
                reraise ()

        entryCount := !entryCount + 1

        if walSize > walRotateBytes || !entryCount > walRotateEntries then
            snapshotNow dataDir store
            entryCount := 0

    store.OnCommit <- Some(fun event -> appendLine ((encodeEvent event).ToJsonString()))

    let onShutdown (_: PosixSignalContext) = snapshotNow dataDir store

    // `PosixSignalRegistration` unregisters its handler when finalized —
    // `attach` returning right after this would leave nothing holding a
    // reference, so the first GC silently stops the SIGTERM/SIGINT
    // shutdown snapshot from ever firing again. Root them for the process's
    // lifetime instead of `|> ignore`-ing the disposable away.
    shutdownRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, onShutdown))
    shutdownRegistrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, onShutdown))
