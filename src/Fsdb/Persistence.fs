/// Opt-in durability (`--data-dir`): WAL + snapshot, replay on startup — M7
/// on the roadmap. Placeholder: subscribes to nothing yet. A real
/// implementation sets `Store.OnCommit` (see `Storage.CommitEvent`) to
/// append each event to a physical WAL file, using `Value.toWire`/`ofWire`
/// to serialize row data, and replays that WAL (plus a snapshot) back into a
/// fresh `Store` on startup.
module Fsdb.Persistence
