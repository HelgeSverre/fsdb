/// FParsec-based SQL parser: raw text in, `Ast.Statement` out.
module Fsdb.Parser

open Fsdb.Ast

let parse (_sql: string) : Result<Statement, string> = Error "parser not implemented"
