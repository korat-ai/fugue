namespace Fugue.Adapters.Console

// =============================================================================
// Live / Status / Progress — Phase 2 (US3)
//
// Callback-scoped session APIs. Session handles are opaque sealed types
// valid only inside the body callback. Lifetime is managed by Spectre's
// own try/finally (cursor restore). The adapter adds:
//   1. Per-console ConcurrentLiveSession enforcement (ConditionalWeakTable).
//   2. Non-TTY fallback for Status and Progress (FR-016).
//   3. Cancellation mapping to Error (UserCancelled "live"|"status"|"progress").
//   4. Custom spinner validation (Spinner.Custom → F# subclass).
// =============================================================================

// ─────────────────────────────────────────────────────────────────────────────
// Spinner DU — defined in the .fs (implementation); .fsi declares the signature.
// ─────────────────────────────────────────────────────────────────────────────

type Spinner =
    | Default
    | Ascii
    | Dots          | Dots2          | Dots3          | Dots4
    | Dots5         | Dots6          | Dots7          | Dots8
    | Dots9         | Dots10         | Dots11         | Dots12
    | Dots8Bit
    | Line          | Line2
    | Pipe          | SimpleDots     | SimpleDotsScrolling
    | Star          | Star2
    | Flip          | Hamburger      | GrowVertical   | GrowHorizontal
    | Balloon       | Balloon2       | Noise
    | Bounce        | BoxBounce      | BoxBounce2
    | Triangle      | Arc            | Circle
    | SquareCorners | CircleQuarters | CircleHalves
    | Squish
    | Toggle        | Toggle2        | Toggle3        | Toggle4
    | Toggle5       | Toggle6        | Toggle7        | Toggle8
    | Toggle9       | Toggle10       | Toggle11       | Toggle12
    | Toggle13
    | Arrow         | Arrow2         | Arrow3
    | BouncingBar   | BouncingBall
    | Smiley        | Monkey         | Hearts         | Clock
    | Earth         | Material       | Moon           | Runner
    | Pong          | Shark          | Dqpb           | Weather
    | Christmas     | Grenade        | Point          | Layer
    | BetaWave      | Aesthetic
    | Custom of frames: string list * interval: int

// ─────────────────────────────────────────────────────────────────────────────
// Private helpers — spinner mapping, session guard, cancellation
// ─────────────────────────────────────────────────────────────────────────────

module private LiveHelpers =

    // ── Custom spinner F# subclass ─────────────────────────────────────────
    // Spinner is abstract in Spectre; we subclass it for the Custom case.
    type FSharpCustomSpinner (frames: string array, intervalMs: int) =
        inherit Spectre.Console.Spinner ()
        override _.Interval : System.TimeSpan =
            System.TimeSpan.FromMilliseconds (float intervalMs)
        override _.Frames : System.Collections.Generic.IReadOnlyList<string> =
            frames :> System.Collections.Generic.IReadOnlyList<string>
        override _.IsUnicode = false

    // ── Spinner.Known reflection accessor ──────────────────────────────────
    // Spinner.Known is an internal nested class inside Spectre.Console.Spinner.
    // We access its public static properties via reflection because the Known
    // nested class is not public in Spectre 0.49.1.
    let private spinnerAsm = typeof<Spectre.Console.Spinner>.Assembly

    // Assembly.GetType returns Type|null under .NET 10 nullability analysis.
    // We resolve it once at module init and fail fast if Spectre API has changed.
    let private knownType : System.Type =
        let t : System.Type | null = spinnerAsm.GetType "Spectre.Console.Spinner+Known"
        match t with
        | null -> failwith "Spectre.Console.Spinner+Known not found — unexpected Spectre version?"
        | ty   -> ty

    let private getKnownSpinner (name: string) : Spectre.Console.Spinner =
        // GetProperty returns PropertyInfo|null under .NET 10 nullability analysis.
        let prop : System.Reflection.PropertyInfo | null =
            knownType.GetProperty (
                name,
                System.Reflection.BindingFlags.Public |||
                System.Reflection.BindingFlags.Static)
        match prop with
        | null -> failwith $"Spinner.Known.{name} not found in Spectre.Console 0.49.1"
        | pi   ->
            // GetValue returns obj|null. Use unbox<T|null> to avoid FS3264
            // (which disallows :?> from obj|null to a non-nullable reference type).
            let valueOrNull : Spectre.Console.Spinner | null = unbox<Spectre.Console.Spinner | null> (pi.GetValue null)
            match valueOrNull with
            | null    -> failwith $"Spinner.Known.{name} downcast returned null"
            | spinner -> spinner

    // ── spinnerToSpectre ────────────────────────────────────────────────────
    let spinnerToSpectre (s: Spinner) : Result<Spectre.Console.Spinner, RenderError> =
        match s with
        | Default           -> Ok (getKnownSpinner "Default")
        | Ascii             -> Ok (getKnownSpinner "Ascii")
        | Dots              -> Ok (getKnownSpinner "Dots")
        | Dots2             -> Ok (getKnownSpinner "Dots2")
        | Dots3             -> Ok (getKnownSpinner "Dots3")
        | Dots4             -> Ok (getKnownSpinner "Dots4")
        | Dots5             -> Ok (getKnownSpinner "Dots5")
        | Dots6             -> Ok (getKnownSpinner "Dots6")
        | Dots7             -> Ok (getKnownSpinner "Dots7")
        | Dots8             -> Ok (getKnownSpinner "Dots8")
        | Dots9             -> Ok (getKnownSpinner "Dots9")
        | Dots10            -> Ok (getKnownSpinner "Dots10")
        | Dots11            -> Ok (getKnownSpinner "Dots11")
        | Dots12            -> Ok (getKnownSpinner "Dots12")
        | Dots8Bit          -> Ok (getKnownSpinner "Dots8Bit")
        | Line              -> Ok (getKnownSpinner "Line")
        | Line2             -> Ok (getKnownSpinner "Line2")
        | Pipe              -> Ok (getKnownSpinner "Pipe")
        | SimpleDots        -> Ok (getKnownSpinner "SimpleDots")
        | SimpleDotsScrolling -> Ok (getKnownSpinner "SimpleDotsScrolling")
        | Star              -> Ok (getKnownSpinner "Star")
        | Star2             -> Ok (getKnownSpinner "Star2")
        | Flip              -> Ok (getKnownSpinner "Flip")
        | Hamburger         -> Ok (getKnownSpinner "Hamburger")
        | GrowVertical      -> Ok (getKnownSpinner "GrowVertical")
        | GrowHorizontal    -> Ok (getKnownSpinner "GrowHorizontal")
        | Balloon           -> Ok (getKnownSpinner "Balloon")
        | Balloon2          -> Ok (getKnownSpinner "Balloon2")
        | Noise             -> Ok (getKnownSpinner "Noise")
        | Bounce            -> Ok (getKnownSpinner "Bounce")
        | BoxBounce         -> Ok (getKnownSpinner "BoxBounce")
        | BoxBounce2        -> Ok (getKnownSpinner "BoxBounce2")
        | Triangle          -> Ok (getKnownSpinner "Triangle")
        | Arc               -> Ok (getKnownSpinner "Arc")
        | Circle            -> Ok (getKnownSpinner "Circle")
        | SquareCorners     -> Ok (getKnownSpinner "SquareCorners")
        | CircleQuarters    -> Ok (getKnownSpinner "CircleQuarters")
        | CircleHalves      -> Ok (getKnownSpinner "CircleHalves")
        | Squish            -> Ok (getKnownSpinner "Squish")
        | Toggle            -> Ok (getKnownSpinner "Toggle")
        | Toggle2           -> Ok (getKnownSpinner "Toggle2")
        | Toggle3           -> Ok (getKnownSpinner "Toggle3")
        | Toggle4           -> Ok (getKnownSpinner "Toggle4")
        | Toggle5           -> Ok (getKnownSpinner "Toggle5")
        | Toggle6           -> Ok (getKnownSpinner "Toggle6")
        | Toggle7           -> Ok (getKnownSpinner "Toggle7")
        | Toggle8           -> Ok (getKnownSpinner "Toggle8")
        | Toggle9           -> Ok (getKnownSpinner "Toggle9")
        | Toggle10          -> Ok (getKnownSpinner "Toggle10")
        | Toggle11          -> Ok (getKnownSpinner "Toggle11")
        | Toggle12          -> Ok (getKnownSpinner "Toggle12")
        | Toggle13          -> Ok (getKnownSpinner "Toggle13")
        | Arrow             -> Ok (getKnownSpinner "Arrow")
        | Arrow2            -> Ok (getKnownSpinner "Arrow2")
        | Arrow3            -> Ok (getKnownSpinner "Arrow3")
        | BouncingBar       -> Ok (getKnownSpinner "BouncingBar")
        | BouncingBall      -> Ok (getKnownSpinner "BouncingBall")
        | Smiley            -> Ok (getKnownSpinner "Smiley")
        | Monkey            -> Ok (getKnownSpinner "Monkey")
        | Hearts            -> Ok (getKnownSpinner "Hearts")
        | Clock             -> Ok (getKnownSpinner "Clock")
        | Earth             -> Ok (getKnownSpinner "Earth")
        | Material          -> Ok (getKnownSpinner "Material")
        | Moon              -> Ok (getKnownSpinner "Moon")
        | Runner            -> Ok (getKnownSpinner "Runner")
        | Pong              -> Ok (getKnownSpinner "Pong")
        | Shark             -> Ok (getKnownSpinner "Shark")
        | Dqpb              -> Ok (getKnownSpinner "Dqpb")
        | Weather           -> Ok (getKnownSpinner "Weather")
        | Christmas         -> Ok (getKnownSpinner "Christmas")
        | Grenade           -> Ok (getKnownSpinner "Grenade")
        | Point             -> Ok (getKnownSpinner "Point")
        | Layer             -> Ok (getKnownSpinner "Layer")
        | BetaWave          -> Ok (getKnownSpinner "BetaWave")
        | Aesthetic         -> Ok (getKnownSpinner "Aesthetic")
        | Custom (frames, interval) ->
            if frames.IsEmpty then
                Error (RenderError.InvalidArgument ("Spinner", "custom spinner requires at least one frame"))
            elif interval <= 0 then
                Error (RenderError.InvalidArgument ("Spinner", "custom spinner interval must be positive"))
            else
                Ok (FSharpCustomSpinner (frames |> List.toArray, interval)
                    :> Spectre.Console.Spinner)

    // ── Per-console session guard ───────────────────────────────────────────
    // A single ConditionalWeakTable maps IAnsiConsole instances to a sentinel
    // obj. Shared across Live.run / Status.run / Progress.run so they mutually
    // exclude each other on the same console (per FR-012 / FR-013).
    let private sessionGuard =
        System.Runtime.CompilerServices.ConditionalWeakTable<
            Spectre.Console.IAnsiConsole, obj> ()

    let tryAcquireSession (backend: Spectre.Console.IAnsiConsole) : bool =
        sessionGuard.TryAdd (backend, obj ())

    let releaseSession (backend: Spectre.Console.IAnsiConsole) : unit =
        sessionGuard.Remove backend |> ignore

    // ── Cancellation helpers ────────────────────────────────────────────────
    let checkCancelled (ct: System.Threading.CancellationToken) =
        if ct.IsCancellationRequested then
            raise (System.OperationCanceledException ct)

    // ── Safe exception message extraction ──────────────────────────────────
    // Under .NET 10 strict nullability, catch-all `| ex ->` binds ex: exn|null.
    // This helper extracts the message safely.
    let exnMessage (ex: exn | null) : string =
        match ex with
        | null    -> "(null exception)"
        | nonNull -> nonNull.Message

// ─────────────────────────────────────────────────────────────────────────────
// LiveSession
// ─────────────────────────────────────────────────────────────────────────────

// Note: [<Sealed>] is redundant on record types (they are always sealed in F#).
// The .fsi annotation is kept for documentation; the .fs omits it per compiler rule.
type LiveSession =
    private { LiveCtx:     Spectre.Console.LiveDisplayContext
              LiveConsole: Console
              mutable LiveDone: bool }

module LiveSession =

    let update (sess: LiveSession) (comp: Composition) : Result<unit, RenderError> =
        if sess.LiveDone then
            Error (RenderError.RenderFailed ("LiveSession", "session used after run() returned"))
        else
            let backend = unbox<Spectre.Console.IAnsiConsole> (ConsoleAccess.backend sess.LiveConsole)
            let ctx =
                RenderContext.create
                    backend.Profile.Width
                    (backend.Profile.Capabilities.ColorSystem <> Spectre.Console.ColorSystem.NoColors)
                    "default"
            match Renderer.toRawAnsi ctx comp with
            | Error e -> Error e
            | Ok s    ->
                try
                    sess.LiveCtx.UpdateTarget (Spectre.Console.Text s)
                    sess.LiveCtx.Refresh ()
                    Ok ()
                with ex ->
                    Error (RenderError.RenderFailed ("LiveSession.update", ex.Message))

    let refresh (sess: LiveSession) : Result<unit, RenderError> =
        if sess.LiveDone then
            Error (RenderError.RenderFailed ("LiveSession", "session used after run() returned"))
        else
            try
                sess.LiveCtx.Refresh ()
                Ok ()
            with ex ->
                Error (RenderError.RenderFailed ("LiveSession.refresh", ex.Message))

// ─────────────────────────────────────────────────────────────────────────────
// Live.run
// ─────────────────────────────────────────────────────────────────────────────

module Live =

    let run
            (console: Console)
            (initial: Composition)
            (cancellation: System.Threading.CancellationToken)
            (body: LiveSession -> Async<Result<'T, RenderError>>)
            : Async<Result<'T, RenderError>> =
        async {
            let backend = unbox<Spectre.Console.IAnsiConsole> (ConsoleAccess.backend console)

            // Pre-cancellation check.
            if cancellation.IsCancellationRequested then
                return Error (RenderError.UserCancelled "live")
            elif not (LiveHelpers.tryAcquireSession backend) then
                // Session guard — reject concurrent live sessions on the same console.
                return Error (RenderError.ConcurrentLiveSession (backend.GetHashCode().ToString "x8"))
            else

            try
                // Build the initial IRenderable for Spectre.
                let ctx0 =
                    RenderContext.create
                        backend.Profile.Width
                        (backend.Profile.Capabilities.ColorSystem <> Spectre.Console.ColorSystem.NoColors)
                        "default"
                let initRenderable : Spectre.Console.Rendering.IRenderable =
                    match Renderer.toRawAnsi ctx0 initial with
                    | Ok s    -> Spectre.Console.Text s :> Spectre.Console.Rendering.IRenderable
                    | Error _ -> Spectre.Console.Text "" :> Spectre.Console.Rendering.IRenderable

                let liveDisplay = new Spectre.Console.LiveDisplay (backend, initRenderable)

                let spectreTask =
                    liveDisplay.StartAsync (
                        System.Func<Spectre.Console.LiveDisplayContext, System.Threading.Tasks.Task<Result<'T, RenderError>>> (
                            fun liveCtx ->
                                let session =
                                    { LiveCtx = liveCtx; LiveConsole = console; LiveDone = false }
                                // Bridge Async → Task for the Spectre callback.
                                Async.StartAsTask (
                                    async {
                                        try
                                            // Check cancellation once inside the body.
                                            LiveHelpers.checkCancelled cancellation
                                            let! result = body session
                                            session.LiveDone <- true
                                            return result
                                        with
                                        | :? System.OperationCanceledException ->
                                            session.LiveDone <- true
                                            return Error (RenderError.UserCancelled "live")
                                        | ex ->
                                            session.LiveDone <- true
                                            return Error (RenderError.RenderFailed ("Live.run", LiveHelpers.exnMessage ex))
                                    },
                                    cancellationToken = cancellation)
                        ))

                try
                    return! Async.AwaitTask spectreTask
                with
                | :? System.OperationCanceledException ->
                    return Error (RenderError.UserCancelled "live")
                | :? System.AggregateException as ae ->
                    let inner = ae.InnerException
                    if not (isNull inner) && inner :? System.OperationCanceledException then
                        return Error (RenderError.UserCancelled "live")
                    else
                        return Error (RenderError.RenderFailed ("Live.run", LiveHelpers.exnMessage inner))
                | ex ->
                    return Error (RenderError.RenderFailed ("Live.run", LiveHelpers.exnMessage ex))
            finally
                LiveHelpers.releaseSession backend
        }

// ─────────────────────────────────────────────────────────────────────────────
// StatusSession
// ─────────────────────────────────────────────────────────────────────────────

type StatusSession =
    private { StatusCtx:     Spectre.Console.StatusContext
              StatusConsole: Console
              mutable StatusDone: bool }

module StatusSession =

    let updateMessage (sess: StatusSession) (msg: SafeText) : Result<unit, RenderError> =
        if sess.StatusDone then
            Error (RenderError.RenderFailed ("StatusSession", "session used after run() returned"))
        else
            try
                sess.StatusCtx.Status <- SafeText.unwrap msg
                Ok ()
            with ex ->
                Error (RenderError.RenderFailed ("StatusSession.updateMessage", ex.Message))

    let updateSpinner (sess: StatusSession) (spinner: Spinner) : Result<unit, RenderError> =
        if sess.StatusDone then
            Error (RenderError.RenderFailed ("StatusSession", "session used after run() returned"))
        else
            match LiveHelpers.spinnerToSpectre spinner with
            | Error e -> Error e
            | Ok s    ->
                try
                    sess.StatusCtx.Spinner <- s
                    Ok ()
                with ex ->
                    Error (RenderError.RenderFailed ("StatusSession.updateSpinner", ex.Message))

// ─────────────────────────────────────────────────────────────────────────────
// Status.run
// ─────────────────────────────────────────────────────────────────────────────

module Status =

    let run
            (console: Console)
            (spinner: Spinner)
            (message: SafeText)
            (cancellation: System.Threading.CancellationToken)
            (body: StatusSession -> Async<Result<'T, RenderError>>)
            : Async<Result<'T, RenderError>> =
        async {
            let backend = unbox<Spectre.Console.IAnsiConsole> (ConsoleAccess.backend console)

            if cancellation.IsCancellationRequested then
                return Error (RenderError.UserCancelled "status")
            else

            match LiveHelpers.spinnerToSpectre spinner with
            | Error e -> return Error e
            | Ok spectreSpinner ->

            // Non-TTY fallback (FR-016): run without animation, emit message once.
            // We use Status.StartAsync with AutoRefresh=false for both TTY and non-TTY
            // so the body always gets a valid StatusSession regardless.
            let isInteractive = Console.isInteractive console

            if not isInteractive then
                // Write message once to the backend.
                try
                    backend.Write (Spectre.Console.Text (SafeText.unwrap message))
                with _ ->
                    ()

            // Session guard — only for interactive (TTY) consoles.
            let sessionAcquired =
                if isInteractive then
                    LiveHelpers.tryAcquireSession backend
                else
                    true  // non-TTY: no guard needed

            if not sessionAcquired then
                return Error (RenderError.ConcurrentLiveSession (backend.GetHashCode().ToString "x8"))
            else

            // For non-TTY we still use StartAsync (with AutoRefresh=false) so
            // the body function can work uniformly.
            let statusObj = Spectre.Console.Status (backend)
            statusObj.Spinner <- spectreSpinner
            statusObj.AutoRefresh <- false

            try
                let spectreTask =
                    statusObj.StartAsync (
                        SafeText.unwrap message,
                        System.Func<Spectre.Console.StatusContext, System.Threading.Tasks.Task<Result<'T, RenderError>>> (
                            fun statusCtx ->
                                let sess =
                                    { StatusCtx = statusCtx; StatusConsole = console; StatusDone = false }
                                Async.StartAsTask (
                                    async {
                                        try
                                            LiveHelpers.checkCancelled cancellation
                                            let! result = body sess
                                            sess.StatusDone <- true
                                            return result
                                        with
                                        | :? System.OperationCanceledException ->
                                            sess.StatusDone <- true
                                            return Error (RenderError.UserCancelled "status")
                                        | ex ->
                                            sess.StatusDone <- true
                                            return Error (RenderError.RenderFailed ("Status.run", LiveHelpers.exnMessage ex))
                                    },
                                    cancellationToken = cancellation)
                        ))
                try
                    return! Async.AwaitTask spectreTask
                with
                | :? System.OperationCanceledException ->
                    return Error (RenderError.UserCancelled "status")
                | :? System.AggregateException as ae ->
                    let inner = ae.InnerException
                    if not (isNull inner) && inner :? System.OperationCanceledException then
                        return Error (RenderError.UserCancelled "status")
                    else
                        return Error (RenderError.RenderFailed ("Status.run", LiveHelpers.exnMessage inner))
                | ex ->
                    return Error (RenderError.RenderFailed ("Status.run", LiveHelpers.exnMessage ex))
            finally
                if isInteractive then
                    LiveHelpers.releaseSession backend
        }

// ─────────────────────────────────────────────────────────────────────────────
// ProgressTask + ProgressSession
// ─────────────────────────────────────────────────────────────────────────────

type ProgressTask =
    private { PtBackend: Spectre.Console.ProgressTask
              mutable PtDone: bool }

type ProgressSession =
    private { PsCtx:     Spectre.Console.ProgressContext
              PsConsole: Console
              mutable PsDone: bool }

module ProgressTask =

    let private checkAlive (pt: ProgressTask) : Result<unit, RenderError> =
        if pt.PtDone then
            Error (RenderError.RenderFailed ("ProgressTask", "session used after run() returned"))
        else
            Ok ()

    let increment (pt: ProgressTask) (delta: double) : Result<unit, RenderError> =
        match checkAlive pt with
        | Error e -> Error e
        | Ok () ->
            try
                pt.PtBackend.Increment delta
                Ok ()
            with ex ->
                Error (RenderError.RenderFailed ("ProgressTask.increment", ex.Message))

    let setValue (pt: ProgressTask) (value: double) : Result<unit, RenderError> =
        match checkAlive pt with
        | Error e -> Error e
        | Ok () ->
            try
                // Spectre ProgressTask.Value is read-only; use Increment by delta.
                let delta = value - pt.PtBackend.Value
                pt.PtBackend.Increment delta
                Ok ()
            with ex ->
                Error (RenderError.RenderFailed ("ProgressTask.setValue", ex.Message))

    let complete (pt: ProgressTask) : Result<unit, RenderError> =
        match checkAlive pt with
        | Error e -> Error e
        | Ok () ->
            try
                let remaining = pt.PtBackend.MaxValue - pt.PtBackend.Value
                if remaining > 0.0 then
                    pt.PtBackend.Increment remaining
                Ok ()
            with ex ->
                Error (RenderError.RenderFailed ("ProgressTask.complete", ex.Message))

    let value (pt: ProgressTask) : double = pt.PtBackend.Value

    let maxValue (pt: ProgressTask) : double = pt.PtBackend.MaxValue

module ProgressSession =

    let addTask (sess: ProgressSession) (description: SafeText) : Result<ProgressTask, RenderError> =
        if sess.PsDone then
            Error (RenderError.RenderFailed ("ProgressSession", "session used after run() returned"))
        else
            try
                let spectreTask = sess.PsCtx.AddTask (SafeText.unwrap description, true, 100.0)
                Ok { PtBackend = spectreTask; PtDone = false }
            with ex ->
                Error (RenderError.RenderFailed ("ProgressSession.addTask", ex.Message))

// ─────────────────────────────────────────────────────────────────────────────
// Progress.run
// ─────────────────────────────────────────────────────────────────────────────

module Progress =

    let run
            (console: Console)
            (cancellation: System.Threading.CancellationToken)
            (body: ProgressSession -> Async<Result<'T, RenderError>>)
            : Async<Result<'T, RenderError>> =
        async {
            let backend = unbox<Spectre.Console.IAnsiConsole> (ConsoleAccess.backend console)

            if cancellation.IsCancellationRequested then
                return Error (RenderError.UserCancelled "progress")
            else

            let isInteractive = Console.isInteractive console

            let sessionAcquired =
                if isInteractive then LiveHelpers.tryAcquireSession backend
                else true

            if not sessionAcquired then
                return Error (RenderError.ConcurrentLiveSession (backend.GetHashCode().ToString "x8"))
            else

            let progressObj = Spectre.Console.Progress (backend)
            Spectre.Console.ProgressExtensions.AutoRefresh (progressObj, false) |> ignore

            try
                let spectreTask =
                    progressObj.StartAsync (
                        System.Func<Spectre.Console.ProgressContext, System.Threading.Tasks.Task<Result<'T, RenderError>>> (
                            fun progressCtx ->
                                let sess =
                                    { PsCtx = progressCtx; PsConsole = console; PsDone = false }
                                Async.StartAsTask (
                                    async {
                                        try
                                            LiveHelpers.checkCancelled cancellation
                                            let! result = body sess
                                            sess.PsDone <- true
                                            return result
                                        with
                                        | :? System.OperationCanceledException ->
                                            sess.PsDone <- true
                                            return Error (RenderError.UserCancelled "progress")
                                        | ex ->
                                            sess.PsDone <- true
                                            return Error (RenderError.RenderFailed ("Progress.run", LiveHelpers.exnMessage ex))
                                    },
                                    cancellationToken = cancellation)
                        ))
                try
                    return! Async.AwaitTask spectreTask
                with
                | :? System.OperationCanceledException ->
                    return Error (RenderError.UserCancelled "progress")
                | :? System.AggregateException as ae ->
                    let inner = ae.InnerException
                    if not (isNull inner) && inner :? System.OperationCanceledException then
                        return Error (RenderError.UserCancelled "progress")
                    else
                        return Error (RenderError.RenderFailed ("Progress.run", LiveHelpers.exnMessage inner))
                | ex ->
                    return Error (RenderError.RenderFailed ("Progress.run", LiveHelpers.exnMessage ex))
            finally
                if isInteractive then
                    LiveHelpers.releaseSession backend
        }
