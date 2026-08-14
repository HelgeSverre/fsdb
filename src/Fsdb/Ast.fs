/// The SQL abstract syntax tree the parser produces and the executor
/// consumes. Types only, no behavior — every case here is data.
module Fsdb.Ast

open Fsdb.Value

/// A comparison/logical/arithmetic binary operator, shared by every
/// `BinOp` node rather than splitting expressions into separate DU cases
/// per operator.
type Op =
    | And
    | Or
    | Eq
    | Neq
    | Lt
    | Lte
    | Gt
    | Gte
    | Add
    | Sub
    | Mul
    | Div

type ColumnType =
    | TTinyInt of unsigned: bool
    | TSmallInt of unsigned: bool
    | TMediumInt of unsigned: bool
    | TInt of unsigned: bool
    | TBigInt of unsigned: bool
    | TChar of length: int
    | TVarchar of length: int
    | TTinyText
    | TText
    | TMediumText
    | TLongText
    | TBinary of length: int
    | TVarBinary of length: int
    | TTinyBlob
    | TBlob
    | TMediumBlob
    | TLongBlob
    /// The allowed value set, stored so `Storage.coerceValue` can validate an
    /// inserted string against it.
    | TEnum of values: string list
    /// Accepted like a string column — ponytail: no comma-set validation
    /// against `values`, add it if a migration actually needs SET semantics
    /// enforced rather than just accepted.
    | TSet of values: string list
    | TDecimal of precision: int * scale: int
    | TDouble
    | TFloat
    | TDate
    | TDateTime
    | TTimestamp
    | TTime
    | TYear
    | TJson

type Expr =
    | Lit of Value
    | Col of name: string
    | QualifiedCol of table: string * column: string
    | BinOp of Op * Expr * Expr
    | Not of Expr
    | IsNull of Expr
    | IsNotNull of Expr
    | Like of Expr * pattern: Expr
    | In of Expr * candidates: Expr list
    | Between of Expr * lo: Expr * hi: Expr
    | FuncCall of name: string * args: Expr list
    /// Minimal `CAST(expr AS type)` — reuses `ColumnType` rather than a
    /// separate cast-target vocabulary, coerced the same way a column of
    /// that type would be (see `Storage.coerceValue`).
    | Cast of Expr * ColumnType
    /// `SELECT *` / `SELECT t.*`.
    | Star

type Direction =
    | Asc
    | Desc

/// One `ORDER BY` key: the expression to sort by and its direction.
type OrderKey = Expr * Direction

/// A column's `DEFAULT`: either a fixed value, or `CURRENT_TIMESTAMP`, which
/// evaluates fresh at insert time rather than once at parse time — kept as
/// its own case instead of a `VString "CURRENT_TIMESTAMP"` sentinel value so
/// storage evaluates it explicitly rather than trying (and failing) to
/// coerce the marker text itself into the column's type.
type ColumnDefault =
    | DConst of Value
    | DCurrentTimestamp

type ColumnDef =
    { Name: string
      Type: ColumnType
      Nullable: bool
      Default: ColumnDefault option
      AutoIncrement: bool
      PrimaryKey: bool
      Unique: bool }

/// A named `[UNIQUE] KEY|INDEX (cols)` — from a `CREATE TABLE` trailing item,
/// `ALTER TABLE ADD INDEX`, `CREATE INDEX`, or a column-level `UNIQUE`
/// modifier (which synthesizes one of these named after the column).
type IndexDef = { Name: string; Columns: string list; Unique: bool }

/// A `CONSTRAINT name FOREIGN KEY (cols) REFERENCES tbl (cols) [ON DELETE
/// ...] [ON UPDATE ...]` — metadata only, stored so `information_schema` can
/// see it; ponytail: no referential-action enforcement yet (no cascading
/// delete/update, no insert-time reference check), add it once a migration's
/// test suite actually depends on the enforcement rather than just the
/// metadata existing.
type ForeignKeyDef =
    { Name: string
      Columns: string list
      RefTable: string
      RefColumns: string list
      OnDelete: string option
      OnUpdate: string option }

/// A `SELECT` projection: the expression and its optional `AS alias`.
type Projection = Expr * string option

/// `FROM [db.]table [[AS] alias]`. A record (not a bare string) so a
/// qualified name (`information_schema.tables`) and an alias have somewhere
/// to live — needed for M4's schema introspection and, later, joins —
/// without another breaking edit to every `Select` call site.
type TableRef =
    { Database: string option
      Table: string
      Alias: string option }

/// A `SELECT` statement's clauses as a record rather than a positional
/// tuple: every clause after `SELECT ... FROM` is optional and grows
/// independently (M5 adds `GroupBy`/`Having`), so a record avoids a breaking
/// edit — and an 8-argument re-spelling at every call site — each time one
/// does.
type SelectStmt =
    { Projections: Projection list
      From: TableRef option
      Where: Expr option
      OrderBy: OrderKey list
      Limit: int option
      Offset: int option }

/// One `ALTER TABLE` action; a statement carries a list of these since
/// MySQL (and Laravel) commonly comma-separates several in one `ALTER
/// TABLE`. `AFTER col` / `FIRST` are accepted by the parser but not carried
/// here — ponytail: column ordering isn't tracked yet, add a position field
/// if a migration's assertion ever depends on physical column order rather
/// than just the column existing.
type AlterAction =
    | AddColumn of ColumnDef
    | DropColumn of column: string
    | ModifyColumn of ColumnDef
    | ChangeColumn of oldName: string * ColumnDef
    | RenameTo of newName: string
    | RenameColumnTo of oldName: string * newName: string
    | AddIndex of IndexDef
    | DropIndexAction of name: string
    | AddForeignKey of ForeignKeyDef
    | DropForeignKey of name: string
    | AddPrimaryKey of columns: string list

type Statement =
    | CreateDatabase of name: string * ifNotExists: bool
    | DropDatabase of name: string * ifExists: bool
    | CreateTable of
        name: string *
        columns: ColumnDef list *
        indexes: IndexDef list *
        foreignKeys: ForeignKeyDef list *
        ifNotExists: bool
    | DropTable of names: string list * ifExists: bool
    | AlterTable of table: string * actions: AlterAction list
    | RenameTable of pairs: (string * string) list
    | CreateIndex of name: string * table: string * columns: string list * unique: bool
    | DropIndexStmt of name: string * table: string
    | Insert of
        table: string *
        columns: string list *
        rows: Expr list list *
        onDuplicateUpdate: (string * Expr) list *
        ignoreDuplicates: bool
    | Select of SelectStmt
    | Update of table: string * assignments: (string * Expr) list * where: Expr option
    | Delete of table: string * where: Expr option
    | Truncate of table: string
