namespace Fsdb

open Fsdb.Storage

[<RequireQualifiedAccess>]
module internal CatalogOverlay =
    let tableKey (database: string) (table: string) =
        database.ToLowerInvariant(), normalizeTableName table

    let merge (catalog: Catalog) (overlay: Catalog) =
        overlay
        |> Map.fold (fun result database tables ->
            result
            |> Map.change database (fun current ->
                current
                |> Option.defaultValue Map.empty
                |> fun existing -> Some(Map.fold (fun state name table -> Map.add name table state) existing tables))) catalog

    let containsTable (catalog: Catalog) (database: string) (table: string) =
        catalog
        |> Map.tryFind (database.ToLowerInvariant())
        |> Option.exists (Map.containsKey (normalizeTableName table))

    let tryTable (catalog: Catalog) (database: string, table: string) =
        catalog
        |> Map.tryFind (database.ToLowerInvariant())
        |> Option.bind (Map.tryFind (normalizeTableName table))

    let setTable (catalog: Catalog) (database: string) (table: string) (value: Table option) =
        let database, table = tableKey database table

        Map.change
            database
            (fun current ->
                let tables = Option.defaultValue Map.empty current

                let updated =
                    match value with
                    | Some item -> Map.add table item tables
                    | None -> Map.remove table tables

                if Map.isEmpty updated then None else Some updated)
            catalog

    /// Restoring a shadowed table retains an original database even when empty.
    let restoreTable (original: Catalog) (catalog: Catalog) database table =
        let database, table = tableKey database table
        match Map.tryFind database original with
        | None -> setTable catalog database table None
        | Some originalTables ->
            catalog
            |> Map.change database (fun current ->
                current
                |> Option.defaultValue Map.empty
                |> Map.change table (fun _ -> Map.tryFind table originalTables)
                |> Some)
