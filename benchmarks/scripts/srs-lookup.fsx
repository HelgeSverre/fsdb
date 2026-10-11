// Usage: dotnet fsi --exec benchmarks/scripts/srs-lookup.fsx /path/to/Fsdb.dll
// CSV fields: SRID, found calls, minimum/median/maximum nanoseconds per call.
open System
open System.Diagnostics
open System.Linq.Expressions
open System.Reflection

let assembly = Assembly.LoadFrom fsi.CommandLineArgs.[1]
let moduleType = assembly.GetType("Fsdb.SpatialReferenceSystems", true)
let method = moduleType.GetMethod("tryFind", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)
if isNull method then failwith "tryFind not found"
let argument = Expression.Parameter(typeof<int>, "srid")
let body = Expression.Convert(Expression.Call(method, argument), typeof<obj>)
let lookup = Expression.Lambda<Func<int,obj>>(body, argument).Compile()
let iterations = 1000000
for srid in [0; 4326; 3395; 26923; 999999] do
    for _ in 1..20000 do ignore (lookup.Invoke(srid))
    let trials =
        [| for _ in 1..5 do
               let mutable found = 0
               let stopwatch = Stopwatch.StartNew()
               for _ in 1..iterations do
                   if not (isNull (lookup.Invoke(srid))) then found <- found + 1
               stopwatch.Stop()
               yield found, stopwatch.Elapsed.TotalMilliseconds * 1000000.0 / float iterations |]
    let numbers = trials |> Array.map snd |> Array.sort
    printfn "%d,%d,%.1f,%.1f,%.1f" srid (fst trials.[0]) numbers.[0] numbers.[2] numbers.[4]
