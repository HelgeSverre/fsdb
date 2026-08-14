[<EntryPoint>]
let main argv =
    Expecto.Tests.runTestsWithCLIArgs [] argv (Expecto.Tests.testList "fsdb" [])
