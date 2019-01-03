﻿namespace MBrace.Core

//  Cloud builder implementation

open System
open System.Runtime.Serialization

open MBrace.Core.Internals

#nowarn "444"

[<AutoOpen>]
module internal BuilderImpl =

    // Implementation of expression builder combinators over Body<'T>

    let inline mkCloud body = new Cloud<'T>(body)
    let inline mkLocal body = new LocalCloud<'T>(body)

    let inline capture (e : 'exn) = ExceptionDispatchInfo.Capture e
    let inline extract (edi : ExceptionDispatchInfo) = edi.Reify(false, false)
    let inline protect f s = try Choice1Of2 <| f s with e -> Choice2Of2 e
    let inline getMetadata (t : 'T) = t.GetType().FullName
    let inline appendToStacktrace functionName (edi : ExceptionDispatchInfo) =
        let entry = sprintf "   at %s" functionName
        edi.AppendToStackTrace entry

    type Continuation<'T> with
        member inline c.Cancel ctx = c.Cancellation ctx (new System.OperationCanceledException())

        member inline c.ContinueWith (ctx, result : ValueOrException<'T>) =
            if result.IsValue then c.Success ctx result.Value
            else c.Exception ctx (capture result.Exception)

        member inline c.ContinueWith2 (ctx, result : ValueOrException<#Cloud<'T>>) =
            if result.IsValue then result.Value.Body ctx c
            else c.Exception ctx (capture result.Exception)

        member inline c.ContinueWith2 (ctx, result : ValueOrException<Body<'T>>) =
            if result.IsValue then result.Value ctx c
            else c.Exception ctx (capture result.Exception)

    type ExecutionContext with
        member inline ctx.IsCancellationRequested = ctx.CancellationToken.IsCancellationRequested

    let inline ret t : Body<'T> = fun ctx cont -> if ctx.IsCancellationRequested then cont.Cancel ctx else cont.Success ctx t
    let inline retFunc (f : unit -> 'T) : Body<'T> = 
        fun ctx cont ->
            if ctx.IsCancellationRequested then cont.Cancel ctx else
            match protect f () with
            | Choice1Of2 t -> cont.Success ctx t
            | Choice2Of2 e -> cont.Exception ctx (capture e)

    let zero : Body<unit> = ret ()

    let inline raiseM e : Body<'T> = fun ctx cont -> if ctx.IsCancellationRequested then cont.Cancel ctx else cont.Exception ctx (capture e)

    let inline fromContinuations (body : Body<'T>) : Body<'T> =
        fun ctx cont ->
            if ctx.IsCancellationRequested then cont.Cancel ctx
            elif Trampoline.IsBindThresholdReached(isContinuationExposed = true) then
                Trampoline.QueueWorkItem(fun () -> body ctx cont)
            else
                body ctx cont
        
    let inline ofAsync (asyncWorkflow : Async<'T>) : Body<'T> = 
        fun ctx cont ->
            if ctx.IsCancellationRequested then cont.Cancel ctx
            elif Trampoline.IsBindThresholdReached(isContinuationExposed = true) then
                Trampoline.QueueWorkItem(fun () -> Async.StartWithContinuations(asyncWorkflow, cont.Success ctx, capture >> cont.Exception ctx, cont.Cancellation ctx, ctx.CancellationToken.LocalToken))
            else
                Async.StartWithContinuations(asyncWorkflow, cont.Success ctx, capture >> cont.Exception ctx, cont.Cancellation ctx, ctx.CancellationToken.LocalToken)

    let inline delay (f : unit -> #Cloud<'T>) (ctx : ExecutionContext) (cont : Continuation<'T>) =
        if ctx.IsCancellationRequested then cont.Cancel ctx
        elif Trampoline.IsBindThresholdReached false then 
            Trampoline.QueueWorkItem (fun () -> cont.ContinueWith2(ctx, ValueOrException.protect f ()))
        else
            cont.ContinueWith2(ctx, ValueOrException.protect f ())
    
    let inline delay' (f : unit -> Body<'T>) (ctx : ExecutionContext) (cont : Continuation<'T>) =
        if ctx.IsCancellationRequested then cont.Cancel ctx
        elif Trampoline.IsBindThresholdReached false then 
            Trampoline.QueueWorkItem (fun () -> cont.ContinueWith2(ctx, ValueOrException.protect f ()))
        else
            cont.ContinueWith2(ctx, ValueOrException.protect f ())

#if EXPLICIT_DELAY
    // provides an explicit FSharpFunc implementation for delayed computation;
    // this is to deal with leaks of internal closure types in serialized cloud workflows.
    [<Sealed; DataContract>]
    type private ExplicitDelayWrapper<'T, 'TCloud when 'TCloud :> Cloud<'T>>(body : unit -> 'TCloud) =
        // F# won't let use inherit OptimizedClosures.FSharpFunc<_,_,_> because of the unit return type
        inherit FSharpFunc<ExecutionContext, Continuation<'T> -> unit> ()

        [<DataMember(Name = "Body")>]
        let body = body

        override __.Invoke(ctx : ExecutionContext) = fun c -> delay body ctx c

    let inline mkExplicitDelay (body : unit -> #Cloud<'T>) : Body<'T> =
        let edw = new ExplicitDelayWrapper<'T,_>(body)
        edw :> obj :?> Body<'T>
#else
    let inline mkExplicitDelay (body : unit -> #Cloud<'T>) : Body<'T> =
        delay body
#endif

    let inline bind (f : Body<'T>) (g : 'T -> #Cloud<'S>) : Body<'S> =
        fun ctx cont ->
            if ctx.IsCancellationRequested then cont.Cancel ctx else
            let cont' = {
                Success = 
                    fun ctx t ->
                        if ctx.IsCancellationRequested then cont.Cancel ctx
                        elif Trampoline.IsBindThresholdReached false then
                            Trampoline.QueueWorkItem(fun () -> cont.ContinueWith2(ctx, ValueOrException.protect g t))
                        else
                            cont.ContinueWith2(ctx, ValueOrException.protect g t)

                Exception = 
                    fun ctx e -> 
                        if ctx.IsCancellationRequested then cont.Cancel ctx
                        elif Trampoline.IsBindThresholdReached false then
                            Trampoline.QueueWorkItem(fun () -> cont.Exception ctx e)
                        else
                            cont.Exception ctx e

                Cancellation = cont.Cancellation
            }

            if Trampoline.IsBindThresholdReached false then 
                Trampoline.QueueWorkItem (fun () -> f ctx cont')
            else
                f ctx cont'

    let inline bind' (f : Body<'T>) (g : 'T -> Body<'S>) : Body<'S> =
        fun ctx cont ->
            if ctx.IsCancellationRequested then cont.Cancel ctx else
            let cont' = {
                Success = 
                    fun ctx t ->
                        if ctx.IsCancellationRequested then cont.Cancel ctx
                        elif Trampoline.IsBindThresholdReached false then
                            Trampoline.QueueWorkItem(fun () -> cont.ContinueWith2(ctx, ValueOrException.protect g t))
                        else
                            cont.ContinueWith2(ctx, ValueOrException.protect g t)

                Exception = 
                    fun ctx e -> 
                        if ctx.IsCancellationRequested then cont.Cancel ctx
                        elif Trampoline.IsBindThresholdReached false then
                            Trampoline.QueueWorkItem(fun () -> cont.Exception ctx e)
                        else
                            cont.Exception ctx e

                Cancellation = cont.Cancellation
            }

            if Trampoline.IsBindThresholdReached false then 
                Trampoline.QueueWorkItem (fun () -> f ctx cont')
            else
                f ctx cont'

    let inline combine (f : Body<'unit>) (g : Body<'T>) : Body<'T> =
        fun ctx cont ->
            if ctx.IsCancellationRequested then cont.Cancel ctx else
            let cont' = {
                Success = 
                    fun ctx _ ->
                        if ctx.IsCancellationRequested then cont.Cancel ctx
                        elif Trampoline.IsBindThresholdReached false then
                            Trampoline.QueueWorkItem(fun () -> g ctx cont)
                        else
                            g ctx cont

                Exception = 
                    fun ctx e -> 
                        if ctx.IsCancellationRequested then cont.Cancel ctx
                        elif Trampoline.IsBindThresholdReached false then
                            Trampoline.QueueWorkItem(fun () -> cont.Exception ctx e)
                        else
                            cont.Exception ctx e

                Cancellation = cont.Cancellation
            }

            if Trampoline.IsBindThresholdReached false then 
                Trampoline.QueueWorkItem (fun () -> f ctx cont')
            else
                f ctx cont'

    let inline tryWith (wf : Body<'T>) (handler : exn -> #Cloud<'T>) : Body<'T> =
        fun ctx cont ->
            if ctx.IsCancellationRequested then cont.Cancel ctx else
            let cont' = {
                Success = 
                    fun ctx t -> 
                        if ctx.IsCancellationRequested then cont.Cancel ctx
                        elif Trampoline.IsBindThresholdReached false then
                            Trampoline.QueueWorkItem(fun () -> cont.Success ctx t)
                        else
                            cont.Success ctx t
                
                Exception =
                    fun ctx edi ->
                        if ctx.IsCancellationRequested then cont.Cancel ctx
                        elif Trampoline.IsBindThresholdReached false then
                            Trampoline.QueueWorkItem(fun () -> cont.ContinueWith2(ctx, ValueOrException.protect handler (extract edi)))
                        else
                            cont.ContinueWith2(ctx, ValueOrException.protect handler (extract edi))

                Cancellation = cont.Cancellation
            }

            if Trampoline.IsBindThresholdReached false then 
                Trampoline.QueueWorkItem (fun () -> wf ctx cont')
            else
                wf ctx cont'


    let inline tryFinally (f : Body<'T>) (finalizer : Body<unit>) : Body<'T> =
        fun ctx cont ->
            if ctx.IsCancellationRequested then cont.Cancel ctx else

            let cont' = {
                Success =
                    fun ctx t -> 
                        if ctx.IsCancellationRequested then cont.Cancel ctx else
                        let cont' = Continuation.map (fun () -> t) cont
                        if Trampoline.IsBindThresholdReached false then
                            Trampoline.QueueWorkItem(fun () -> finalizer ctx cont')
                        else
                            finalizer ctx cont'

                Exception = 
                    fun ctx edi -> 
                        if ctx.IsCancellationRequested then cont.Cancel ctx else
                        let cont' = Continuation.failwith (fun () -> (extract edi)) cont
                        if Trampoline.IsBindThresholdReached false then
                            Trampoline.QueueWorkItem(fun () -> finalizer ctx cont')
                        else
                            finalizer ctx cont'

                Cancellation = cont.Cancellation
            }

            if Trampoline.IsBindThresholdReached false then 
                Trampoline.QueueWorkItem (fun () -> f ctx cont')
            else
                f ctx cont'

    let inline dispose (d : ICloudDisposable) = 
        ofAsync (async { if not <| obj.ReferenceEquals(d, null) then return! d.Dispose() })

    let inline usingIDisposable (t : #IDisposable) (g : #IDisposable -> #Cloud<'S>) : Body<'S> =
        tryFinally (bind ((ret t)) g) (retFunc (fun () -> if not <| obj.ReferenceEquals(t, null) then t.Dispose()))

    let inline usingICloudDisposable (t : #ICloudDisposable) (g : #ICloudDisposable -> #Cloud<'S>) : Body<'S> =
        tryFinally (bind (ret t) g) (dispose t)

    let inline forArray (body : 'T -> #Cloud<unit>) (ts : 'T []) : Body<unit> =
        let rec loop i () =
            if i = ts.Length then zero
            else
                match protect body ts.[i] with
                | Choice1Of2 b -> bind' b.Body (loop (i+1))
                | Choice2Of2 e -> raiseM e

        delay' (loop 0)

    let inline forList (body : 'T -> #Cloud<unit>) (ts : 'T list) : Body<unit> =
        let rec loop ts () =
            match ts with
            | [] -> zero
            | t :: ts ->
                match protect body t with
                | Choice1Of2 b -> bind' b.Body (loop ts)
                | Choice2Of2 e -> raiseM e

        delay' (loop ts)

    let inline forSeq (body : 'T -> #Cloud<unit>) (ts : seq<'T>) : Body<unit> =
        delay' <|
            fun () ->
                let enum = ts.GetEnumerator()
                let rec loop () =
                    if enum.MoveNext() then
                        match protect body enum.Current with
                        | Choice1Of2 b -> bind' b.Body loop
                        | Choice2Of2 e -> raiseM e
                    else
                        ret ()

                tryFinally (loop ()) (retFunc enum.Dispose)

    let inline whileM (pred : unit -> bool) (body : Body<unit>) : Body<unit> =
        let rec loop () =
            match protect pred () with
            | Choice1Of2 true -> bind' body loop
            | Choice1Of2 false -> zero
            | Choice2Of2 e -> raiseM e

        delay' loop

    // wraps workflow in a nested execution context which can be updated
    // once computation is completed.
    let inline withNestedContext (update : ExecutionContext -> ExecutionContext)
                                  (revert : ExecutionContext -> ExecutionContext) 
                                  (body : Body<'T>) : Body<'T> =
        fun ctx cont ->
            match protect update ctx with
            | Choice1Of2 ctx' -> 
                // update immediate continuation so that execution context is reverted as soon as provided workflow is completed.
                let cont' =
                    { 
                        Success = fun ctx t -> match protect revert ctx with Choice1Of2 ctx' -> cont.Success ctx' t | Choice2Of2 e -> cont.Exception ctx (capture e)
                        Exception = fun ctx e -> match protect revert ctx with Choice1Of2 ctx' -> cont.Exception ctx' e | Choice2Of2 e -> cont.Exception ctx (capture e)
                        Cancellation = fun ctx c -> match protect revert ctx with Choice1Of2 ctx' -> cont.Cancellation ctx' c | Choice2Of2 _ -> cont.Cancellation ctx c
                    }

                body ctx' cont'
            | Choice2Of2 e -> cont.Exception ctx (capture e)

/// A collection of builder implementations for MBrace workflows
[<AutoOpen>]
module Builders =

    /// Represents a workflow builder used to specify distributed cloud computations. 
    type CloudBuilder () =
        let czero : Cloud<unit> = mkCloud zero
        member __.Return (t : 'T) : Cloud<'T> = mkCloud <| ret t
        member __.Zero () : Cloud<unit> = czero
        member __.Delay (f : unit -> Cloud<'T>) : Cloud<'T> = mkCloud <| mkExplicitDelay f
        member __.ReturnFrom (c : Cloud<'T>) : Cloud<'T> = c
        member __.Combine(f : Cloud<unit>, g : Cloud<'T>) : Cloud<'T> = mkCloud <| combine f.Body g.Body
        member __.Bind (f : Cloud<'T>, g : 'T -> Cloud<'S>) : Cloud<'S> = mkCloud <| bind f.Body g

        member __.Using<'T, 'U when 'T :> ICloudDisposable>(value : 'T, bindF : 'T -> Cloud<'U>) : Cloud<'U> = 
            mkCloud <| usingICloudDisposable value bindF

        [<CompilerMessage("Use of System.IDisposable bindings in cloud { ... } workflows is not recommended because these values are normally not serializable. Consider using a more constrained local { ... } cloud workflow instead, which are constrained to execute on a single machine. Note that all cloud computations which manipulate local resources should normally be written using local { ... } cloud workflows.", 443)>]
        member __.Using<'T, 'U, 'p when 'T :> IDisposable>(value : 'T, bindF : 'T -> Cloud<'U>) : Cloud<'U> = 
            mkCloud <| usingIDisposable value bindF

        member __.TryWith(f : Cloud<'T>, handler : exn -> Cloud<'T>) : Cloud<'T> = mkCloud <| tryWith f.Body handler
        member __.TryFinally(f : Cloud<'T>, finalizer : unit -> unit) : Cloud<'T> = 
            mkCloud <| tryFinally f.Body (retFunc finalizer)

        member __.For(ts : 'T [], body : 'T -> Cloud<unit>) : Cloud<unit> = mkCloud <| forArray body ts
        member __.For(ts : 'T list, body : 'T -> Cloud<unit>) : Cloud<unit> = mkCloud <| forList body ts
        [<CompilerMessage("For loops indexed on IEnumerable are not recommended in cloud { ... } workflows because arbitrary IEnumerable values are often not serializable. Consider either explicitly converting to list or array, or using a more constrained local { ... } cloud workflow instead. These are constrained to execute on a single machine. Note that all cloud computations which manipulate local memory or unserializable resources should normally be written using local { ... } cloud workflows.", 443)>]
        member __.For(ts : seq<'T>, body : 'T -> Cloud<unit>) : Cloud<unit> = 
            match ts with
            | :? ('T []) as ts -> mkCloud <| forArray body ts
            | :? ('T list) as ts -> mkCloud <| forList body ts
            | _ -> mkCloud <| forSeq body ts

        [<CompilerMessage("While loops are not recommended in cloud { ... } workflows because they normally manipulate unserializable shared memory. Consider using a more constrained local { ... } cloud workflow instead. These are constrained to execute on a single machine. Note that all cloud computations which manipulate local memory or unserializable resources should normally be written using local { ... } cloud workflows.", 443)>]
        member __.While(pred : unit -> bool, body : Cloud<unit>) : Cloud<unit> = mkCloud <| whileM pred body.Body

    /// Represents a workflow builder used to specify single-machine cloud computations. 
    type CloudLocalBuilder () =
        let lzero : LocalCloud<unit> = mkLocal zero
        member __.Return (t : 'T) : LocalCloud<'T> = mkLocal <| ret t
        member __.Zero () : LocalCloud<unit> = lzero
        member __.Delay (f : unit -> LocalCloud<'T>) : LocalCloud<'T> = mkLocal <| mkExplicitDelay f
        member __.ReturnFrom (c : LocalCloud<'T>) : LocalCloud<'T> = c
        member __.Combine(f : LocalCloud<unit>, g : LocalCloud<'T>) : LocalCloud<'T> = mkLocal <| combine f.Body g.Body
        member __.Bind (f : LocalCloud<'T>, g : 'T -> LocalCloud<'S>) : LocalCloud<'S> = mkLocal <| bind f.Body g

        member __.Using<'T, 'U, 'p when 'T :> IDisposable>(value : 'T, bindF : 'T -> LocalCloud<'U>) : LocalCloud<'U> = 
            mkLocal <| usingIDisposable value bindF
        member __.Using<'T, 'U when 'T :> ICloudDisposable>(value : 'T, bindF : 'T -> LocalCloud<'U>) : LocalCloud<'U> = 
            mkLocal <| usingICloudDisposable value bindF

        member __.TryWith(f : LocalCloud<'T>, handler : exn -> LocalCloud<'T>) : LocalCloud<'T> = mkLocal <| tryWith f.Body handler
        member __.TryFinally(f : LocalCloud<'T>, finalizer : unit -> unit) : LocalCloud<'T> = 
            mkLocal <| tryFinally f.Body (retFunc finalizer)

        member __.For(ts : seq<'T>, body : 'T -> LocalCloud<unit>) : LocalCloud<unit> = 
            match ts with
            | :? ('T []) as ts -> mkLocal <| forArray body ts
            | :? ('T list) as ts -> mkLocal <| forList body ts
            | _ -> mkLocal <| forSeq body ts

        member __.While(pred : unit -> bool, body : LocalCloud<unit>) : LocalCloud<unit> = mkLocal <| whileM pred body.Body


    /// A workflow builder used to specify single-machine cloud computations. The
    /// computation runs to completion as a locally executing in-memory 
    /// computation. The computation may access concurrent shared memory and 
    /// unserializable resources.  
    let local = new CloudLocalBuilder ()

    /// A workflow builder used to specify distributed cloud computations. Constituent parts of the computation
    /// may be serialized, scheduled and may migrate between different distributed 
    /// execution contexts. If the computation accesses concurrent shared memory then
    /// replace by a more constrained "local" workflow.  
    let cloud = new CloudBuilder ()


/// Collection of MBrace builder extensions for seamlessly
/// composing MBrace with asynchronous workflows
module BuilderAsyncExtensions =

    type CloudLocalBuilder with
        member __.Bind(f : Async<'T>, g : 'T -> LocalCloud<'S>) : LocalCloud<'S> =
            mkLocal <| bind (ofAsync f) g

        member __.ReturnFrom(f : Async<'T>) : LocalCloud<'T> =
            mkLocal <| ofAsync f    
    
    type CloudBuilder with
        member __.Bind(f : Async<'T>, g : 'T -> Cloud<'S>) : Cloud<'S> =
            mkCloud <| bind (ofAsync f) g

        member __.ReturnFrom(f : Async<'T>) : Cloud<'T> =
            mkCloud <| ofAsync f