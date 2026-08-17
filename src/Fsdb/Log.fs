/// Where fsdb's diagnostic stderr output goes — connection drops, WAL replay
/// warnings, query errors surfaced server-side, not the wire-protocol error
/// sent to the client. One mutable sink so tests can silence or capture it
/// without an interface/DI layer for a single function.
module Fsdb.Log

let mutable private sink: string -> unit = eprintfn "%s"

/// Points every diagnostic at `f` instead of stderr — tests use this to
/// capture or silence output; embedders use `Db.withLogger`.
let useSink (f: string -> unit) : unit = sink <- f

/// Drops every diagnostic on the floor.
let silence () : unit = sink <- ignore

/// Formats and routes one diagnostic line through the current sink.
let diagnostic fmt = Printf.kprintf sink fmt
