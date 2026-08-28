module Fsdb.SqlState

type Error =
    { Code: int
      State: string
      Message: string
      Information: Map<string, string> }

let forCode =
    function
    | 1040 -> "08004"
    | 1047
    | 1153 -> "08S01"
    | 1048
    | 1052
    | 1062
    | 1451
    | 1452 -> "23000"
    | 1054 -> "42S22"
    | 1064
    | 1071
    | 1074
    | 1235
    | 1324
    | 1327
    | 1330
    | 1331
    | 1332
    | 1333
    | 1337
    | 1338
    | 1413
    | 1426
    | 3948 -> "42000"
    | 1146 -> "42S02"
    | 1264
    | 1690 -> "22003"
    | 1265 -> "01000"
    | 1325
    | 1326 -> "24000"
    | 1758 -> "35000"
    | _ -> "HY000"

let create code message =
    { Code = code
      State = forCode code
      Message = message
      Information = Map.empty }

let createWithState code state message =
    { Code = code
      State = state
      Message = message
      Information = Map.empty }

let createDetailed code state message information =
    { Code = code
      State = state
      Message = message
      Information = information }
