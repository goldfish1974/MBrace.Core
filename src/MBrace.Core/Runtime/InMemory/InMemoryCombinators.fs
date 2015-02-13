﻿namespace MBrace.Runtime.InMemory

#nowarn "444"

open System.Threading
open System.Threading.Tasks

open MBrace
open MBrace.Continuation

///// Collection of workflows that emulate execute
///// the parallelism primitives sequentially.
//type Sequential =
//
//    /// <summary>
//    ///     A Cloud.Parallel implementation executed sequentially.
//    /// </summary>
//    /// <param name="computations">Input computations.</param>
//    [<CompilerMessage("Use of Sequential.Parallel restricted to runtime implementers.", 444)>]
//    static member Parallel(computations : seq<Workflow<'T>>) : Cloud<'T []> = cloud {
//        let arr = ResizeArray<'T> ()
//        for comp in Seq.toArray computations do
//            let! r = comp in arr.Add r
//        return arr.ToArray()
//    }
//
//    /// <summary>
//    ///     A Cloud.Choice implementation executed sequentially.
//    /// </summary>
//    /// <param name="computations">Input computations.</param>
//    [<CompilerMessage("Use of Sequential.Choice restricted to runtime implementers.", 444)>]
//    static member Choice(computations : seq<Workflow<'T option>>) : Cloud<'T option> = cloud {
//        let computations = Seq.toArray computations
//        let rec aux i = cloud {
//            if i = computations.Length then return None
//            else
//                let! r = computations.[i]
//                match r with
//                | None -> return! aux (i+1)
//                | Some _ -> return r
//        }
//
//        return! aux 0
//    }

/// Collection of workflows that provide parallelism
/// using the .NET thread pool
type ThreadPool private () =

    static let scheduleTask res ct sc ec cc wf =
        Trampoline.QueueWorkItem(fun () ->
            let ctx = { Resources = res ; CancellationToken = ct }
            let cont = { Success = sc ; Exception = ec ; Cancellation = cc }
            Cloud.StartWithContinuations(wf, cont, ctx))

    /// <summary>
    ///     A Cloud.Parallel implementation executed using the thread pool.
    /// </summary>
    /// <param name="mkNestedCts">Creates a child cancellation token source for child workflows.</param>
    /// <param name="computations">Input computations.</param>
    [<CompilerMessage("Use of ThreadPool.Parallel restricted to runtime implementers.", 444)>]
    static member Parallel (mkNestedCts : ICloudCancellationToken -> ICloudCancellationTokenSource, computations : seq<#Workflow<'T>>) : Workflow<_, 'T []> =
        Cloud.FromContinuations(fun ctx cont ->
            match (try Seq.toArray computations |> Choice1Of2 with e -> Choice2Of2 e) with
            | Choice2Of2 e -> cont.Exception ctx (ExceptionDispatchInfo.Capture e)
            | Choice1Of2 [||] -> cont.Success ctx [||]
            // pass continuation directly to child, if singular
            | Choice1Of2 [| comp |] ->
                let cont' = Continuation.map (fun t -> [| t |]) cont
                Cloud.StartWithContinuations(comp, cont', ctx)

            | Choice1Of2 computations ->                    
                let results = Array.zeroCreate<'T> computations.Length
                let parentCt = ctx.CancellationToken
                let innerCts = mkNestedCts parentCt
                let exceptionLatch = new Latch(0)
                let completionLatch = new Latch(0)

                let inline revertCtx (ctx : ExecutionContext) = { ctx with CancellationToken = parentCt }

                let onSuccess i ctx (t : 'T) =
                    results.[i] <- t
                    if completionLatch.Increment() = results.Length then
                        innerCts.Cancel()
                        cont.Success (revertCtx ctx) results

                let onException ctx edi =
                    if exceptionLatch.Increment() = 1 then
                        innerCts.Cancel ()
                        cont.Exception (revertCtx ctx) edi

                let onCancellation ctx c =
                    if exceptionLatch.Increment() = 1 then
                        innerCts.Cancel ()
                        cont.Cancellation (revertCtx ctx) c

                for i = 0 to computations.Length - 1 do
                    scheduleTask ctx.Resources innerCts.Token (onSuccess i) onException onCancellation computations.[i])

    /// <summary>
    ///     A Cloud.Choice implementation executed using the thread pool.
    /// </summary>
    /// <param name="mkNestedCts">Creates a child cancellation token source for child workflows.</param>
    /// <param name="computations">Input computations.</param>
    [<CompilerMessage("Use of ThreadPool.Choice restricted to runtime implementers.", 444)>]
    static member Choice(mkNestedCts : ICloudCancellationToken -> ICloudCancellationTokenSource, computations : seq<#Workflow<'T option>>) : Workflow<_, 'T option> =
        Cloud.FromContinuations(fun ctx cont ->
            match (try Seq.toArray computations |> Choice1Of2 with e -> Choice2Of2 e) with
            | Choice2Of2 e -> cont.Exception ctx (ExceptionDispatchInfo.Capture e)
            | Choice1Of2 [||] -> cont.Success ctx None
            // pass continuation directly to child, if singular
            | Choice1Of2 [| comp |] -> Cloud.StartWithContinuations(comp, cont, ctx)
            | Choice1Of2 computations ->
                let parentCt = ctx.CancellationToken
                let innerCts = mkNestedCts parentCt
                let completionLatch = new Latch(0)
                let exceptionLatch = new Latch(0)

                let inline revertCtx (ctx : ExecutionContext) = { ctx with CancellationToken = parentCt }

                let onSuccess ctx (topt : 'T option) =
                    if Option.isSome topt then
                        if exceptionLatch.Increment() = 1 then
                            innerCts.Cancel()
                            cont.Success (revertCtx ctx) topt
                    else
                        if completionLatch.Increment () = computations.Length then
                            innerCts.Cancel()
                            cont.Success (revertCtx ctx) None

                let onException ctx edi =
                    if exceptionLatch.Increment() = 1 then
                        innerCts.Cancel ()
                        cont.Exception (revertCtx ctx) edi

                let onCancellation ctx cdi =
                    if exceptionLatch.Increment() = 1 then
                        innerCts.Cancel ()
                        cont.Cancellation (revertCtx ctx) cdi

                for i = 0 to computations.Length - 1 do
                    scheduleTask ctx.Resources innerCts.Token onSuccess onException onCancellation computations.[i])