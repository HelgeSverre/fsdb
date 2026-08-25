namespace Fsdb

open System
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
module internal GroupCommit =
    type private Work<'T> =
        | Write of value: 'T * completion: TaskCompletionSource<unit>
        | Checkpoint of completion: TaskCompletionSource<unit>

    let private completion () =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let private wait (completion: TaskCompletionSource<unit>) =
        completion.Task.GetAwaiter().GetResult()

    /// Batches writes that arrive while the preceding flush is in progress.
    [<Sealed>]
    type Queue<'T>(capacity: int, writeBatch: 'T list -> unit, checkpoint: unit -> unit) =
        do
            if capacity <= 0 then
                invalidArg (nameof capacity) "Group-commit capacity must be positive."

        let gate = obj ()
        let pending = System.Collections.Generic.Queue<Work<'T>>()
        let mutable hasLeader = false
        let mutable failure: exn option = None

        let fail (work: Work<'T>) (error: exn) =
            match work with
            | Write(_, completion)
            | Checkpoint completion -> completion.TrySetException error |> ignore

        let completeWrites (writes: ('T * TaskCompletionSource<unit>) list) =
            match writes with
            | [] -> ()
            | writes ->
                writeBatch (writes |> List.map fst)
                writes |> List.iter (snd >> fun completion -> completion.TrySetResult() |> ignore)

        let processWork (work: Work<'T> list) =
            let rec loop (writes: ('T * TaskCompletionSource<unit>) list) =
                function
                | [] -> completeWrites (List.rev writes)
                | Write(value, completion) :: rest -> loop ((value, completion) :: writes) rest
                | Checkpoint completion :: rest ->
                    completeWrites (List.rev writes)
                    checkpoint ()
                    completion.TrySetResult() |> ignore
                    loop [] rest

            loop [] work

        let rec drain () =
            let work =
                lock gate (fun () ->
                    if pending.Count = 0 then
                        hasLeader <- false
                        []
                    else
                        let work = [ while pending.Count > 0 do pending.Dequeue() ]
                        Monitor.PulseAll gate
                        work)

            match work with
            | [] -> ()
            | work ->
                try
                    processWork work
                    drain ()
                with error ->
                    let abandoned =
                        lock gate (fun () ->
                            failure <- Some error
                            hasLeader <- false
                            let abandoned = [ while pending.Count > 0 do pending.Dequeue() ]
                            Monitor.PulseAll gate
                            abandoned)

                    work |> List.iter (fun item -> fail item error)
                    abandoned |> List.iter (fun item -> fail item error)

        let enqueue (work: Work<'T>) (completion: TaskCompletionSource<unit>) =
            let leader =
                lock gate (fun () ->
                    while pending.Count >= capacity && failure.IsNone do
                        Monitor.Wait gate |> ignore

                    match failure with
                    | Some error -> raise error
                    | None ->
                        pending.Enqueue work

                        if hasLeader then
                            false
                        else
                            hasLeader <- true
                            true)

            fun () ->
                if leader then
                    drain ()

                wait completion

        member _.Enqueue(value: 'T) : unit -> unit =
            let completion = completion ()
            enqueue (Write(value, completion)) completion

        member _.EnqueueCheckpoint() : unit -> unit =
            let completion = completion ()
            enqueue (Checkpoint completion) completion
