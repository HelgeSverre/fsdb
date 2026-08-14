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
    /// `SELECT *` / `SELECT t.*`.
    | Star

type Direction =
    | Asc
    | Desc

/// One `ORDER BY` key: the expression to sort by and its direction.
type OrderKey = Expr * Direction

type ColumnType =
    | TInt
    | TBigInt of unsigned: bool
    | TTinyInt
    | TVarchar of length: int
    | TText
    | TDecimal of precision: int * scale: int
    | TDouble
    | TDate
    | TDateTime
    | TTimestamp
    | TJson
    | TBool

type ColumnDef =
    { Name: string
      Type: ColumnType
      Nullable: bool
      Default: Value option
      AutoIncrement: bool
      PrimaryKey: bool }

/// A `SELECT` projection: the expression and its optional `AS alias`.
type Projection = Expr * string option

type Statement =
    | CreateTable of name: string * columns: ColumnDef list * ifNotExists: bool
    | DropTable of names: string list * ifExists: bool
    | Insert of table: string * columns: string list * rows: Expr list list
    | Select of
        projections: Projection list *
        from: string option *
        where: Expr option *
        orderBy: OrderKey list *
        limit: int option *
        offset: int option
    | Update of table: string * assignments: (string * Expr) list * where: Expr option
    | Delete of table: string * where: Expr option
    | Truncate of table: string
