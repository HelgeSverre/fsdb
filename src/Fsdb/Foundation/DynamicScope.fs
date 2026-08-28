/// Scoped values carried through asynchronous execution.
module internal Fsdb.DynamicScope

open System.Threading

let valueOrDefault fallback (slot: AsyncLocal<'value>) =
    if isNull (box slot.Value) then fallback else slot.Value

let getOrCreate factory (slot: AsyncLocal<'value>) =
    if isNull (box slot.Value) then
        let value = factory ()
        slot.Value <- value
        value
    else
        slot.Value

let withValue (slot: AsyncLocal<'value>) (value: 'value) (body: unit -> 'result) : 'result =
    let previous = slot.Value
    slot.Value <- value

    try
        body ()
    finally
        slot.Value <- previous
