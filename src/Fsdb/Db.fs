/// The embedding facade — the one door into fsdb for host code, SQLite-
/// style: create a `Db`, register custom functions, listen.
module Fsdb.Db

open System.Net
open Fsdb.Functions

/// An embeddable fsdb instance: its storage plus whatever custom functions
/// have been registered so far. Immutable, like every other piece of fsdb's
/// state — `registerScalar`/`registerAggregate` return a new `Db` so calls
/// chain with `|>`.
type Db = { Store: Storage.Store; Functions: Registry }

/// A fresh, empty database: no data, no custom functions.
let create () : Db =
    { Store = Storage.create (); Functions = Functions.empty }

/// Registers a scalar function under `name`, e.g.
/// `db |> Db.registerScalar "slugify" (function ...)`. Free to override a
/// built-in of the same name — `QueryHandler.registryFor` layers custom
/// functions over the built-ins, under session-bound ones like `DATABASE()`.
let registerScalar (name: string) (fn: Scalar) (db: Db) : Db =
    { db with Functions = registerScalar name fn db.Functions }

/// Registers an aggregate function under `name`, e.g.
/// `db |> Db.registerAggregate "median" (fun values -> ...)`.
let registerAggregate (name: string) (fn: Aggregate) (db: Db) : Db =
    { db with Functions = registerAggregate name fn db.Functions }

/// Starts the MySQL wire-protocol server on `address:port` and serves
/// connections until the process ends — `db`'s custom functions are
/// available to every statement any connection runs.
let listen (address: IPAddress) (port: int) (db: Db) : Async<unit> =
    let listener = Server.startListening address port
    Server.serve listener db.Store db.Functions
