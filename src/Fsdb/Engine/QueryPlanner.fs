module Fsdb.QueryPlanner

type AccessPath =
    | TableScan
    | IndexLookup
    | IndexRange

type private AccessEstimate =
    { Path: AccessPath
      RowsRead: int
      RowLookups: int }

let private work estimate =
    int64 estimate.RowsRead + int64 estimate.RowLookups

let private rangePreference estimate =
    match estimate.Path, estimate.RowsRead with
    | IndexRange, 0 -> 0
    | TableScan, _ -> 1
    | IndexRange, _ -> 2
    | IndexLookup, _ -> 3

let private equalityPreference estimate =
    match estimate.Path, estimate.RowsRead with
    | IndexLookup, 0 -> 0
    | IndexLookup, _ -> 1
    | TableScan, _ -> 2
    | IndexRange, _ -> 3

let private choose preference estimates =
    estimates
    |> List.minBy (fun estimate -> work estimate, preference estimate)
    |> _.Path

let chooseRange tableRows candidateRows =
    [ { Path = TableScan
        RowsRead = max 0 tableRows
        RowLookups = 0 }
      { Path = IndexRange
        RowsRead = max 0 candidateRows
        RowLookups = max 0 candidateRows } ]
    |> choose rangePreference

let chooseEquality tableRows candidateRows =
    [ { Path = TableScan
        RowsRead = max 0 tableRows
        RowLookups = 0 }
      { Path = IndexLookup
        RowsRead = max 0 candidateRows
        RowLookups = max 0 candidateRows } ]
    |> choose equalityPreference
