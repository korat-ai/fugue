module Fugue.Adapters.Console.Tests.LiveTests

// see Helpers.fs FACT_DISCOVERY for why all tests are [<Property>]
open FsCheck.Xunit
open Fugue.Adapters.Console
open System.Threading

// ============================================================================
// Helpers
// ============================================================================

let private testConsole () = Console.test 80 false

let private emptyComp = Composition.stack []

let private textComp (s: string) =
    Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral s))

// Runs an Async<Result<'T, RenderError>> synchronously for test assertions.
let private runSync (a: Async<Result<'T, RenderError>>) : Result<'T, RenderError> =
    Async.RunSynchronously a

// ============================================================================
// T036 — Live.run happy path
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T036a — Live.run body executes and returns Ok`` () =
    let c  = testConsole ()
    let ct = CancellationToken.None
    let result =
        Live.run c emptyComp ct (fun sess ->
            async {
                let _ = LiveSession.update sess emptyComp
                return Ok 42
            })
        |> runSync
    match result with
    | Ok 42 -> true
    | Ok v  -> failwith $"Expected Ok 42 but got Ok %A{v}"
    | Error e -> failwith $"Expected Ok 42 but got Error: %A{e}"

[<Property(MaxTest = 1)>]
let ``T036b — Live.run with initial composition returns Ok`` () =
    let c    = testConsole ()
    let init = textComp "initial"
    let ct   = CancellationToken.None
    let result =
        Live.run c init ct (fun _ -> async { return Ok "done" })
        |> runSync
    match result with
    | Ok "done" -> true
    | Ok v      -> failwith $"Expected Ok \"done\" but got Ok %A{v}"
    | Error e   -> failwith $"Expected Ok but got Error: %A{e}"

// ============================================================================
// T037 — Live.run cancellation
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T037 — Live.run with pre-cancelled token returns Error UserCancelled`` () =
    let c    = testConsole ()
    let cts  = new CancellationTokenSource ()
    cts.Cancel ()
    let result =
        Live.run c emptyComp cts.Token (fun _ -> async { return Ok () })
        |> runSync
    match result with
    | Error (RenderError.UserCancelled "live") -> true
    | Error e -> failwith $"Expected UserCancelled \"live\" but got Error: %A{e}"
    | Ok _    -> failwith "Expected Error but got Ok"

// ============================================================================
// T038 — Live.run concurrent session guard
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T038 — Live.run returns ConcurrentLiveSession if same console already active`` () =
    let c  = testConsole ()
    let ct = CancellationToken.None
    let mutable innerResult : Result<unit, RenderError> = Ok ()
    // Outer run holds the session; inner run on the same console should fail.
    let outerResult =
        Live.run c emptyComp ct (fun _ ->
            async {
                let inner =
                    Live.run c emptyComp ct (fun _ -> async { return Ok () })
                    |> runSync
                innerResult <- inner
                return Ok ()
            })
        |> runSync
    match outerResult with
    | Error e -> failwith $"Outer Live.run failed unexpectedly: %A{e}"
    | Ok () ->
        match innerResult with
        | Error (RenderError.ConcurrentLiveSession _) -> true
        | Error e -> failwith $"Expected ConcurrentLiveSession but got: %A{e}"
        | Ok _    -> failwith "Expected ConcurrentLiveSession error but got Ok"

// ============================================================================
// T039 — LiveSession.update
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T039a — LiveSession.update returns Ok with a valid composition`` () =
    let c  = testConsole ()
    let ct = CancellationToken.None
    let result =
        Live.run c emptyComp ct (fun sess ->
            async {
                let r = LiveSession.update sess (textComp "updated")
                return r |> Result.map (fun () -> true)
            })
        |> runSync
    match result with
    | Ok true -> true
    | Ok v    -> failwith $"Unexpected Ok value: %A{v}"
    | Error e -> failwith $"Expected Ok but got Error: %A{e}"

[<Property(MaxTest = 1)>]
let ``T039b — LiveSession.update after session ends returns RenderFailed`` () =
    let c  = testConsole ()
    let ct = CancellationToken.None
    let mutable sess : LiveSession option = None
    // Run to completion, capturing the session handle.
    let _ =
        Live.run c emptyComp ct (fun s ->
            async {
                sess <- Some s
                return Ok ()
            })
        |> runSync
    // Now the session has ended — update must return Error.
    match sess with
    | None   -> failwith "Session was never captured"
    | Some s ->
        match LiveSession.update s emptyComp with
        | Error (RenderError.RenderFailed ("LiveSession", _)) -> true
        | Error e -> failwith $"Expected RenderFailed(LiveSession) but got: %A{e}"
        | Ok _    -> failwith "Expected Error after session ended but got Ok"

// ============================================================================
// T040 — LiveSession.refresh
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T040a — LiveSession.refresh inside body returns Ok`` () =
    let c  = testConsole ()
    let ct = CancellationToken.None
    let result =
        Live.run c emptyComp ct (fun sess ->
            async {
                let r = LiveSession.refresh sess
                return r |> Result.map (fun () -> true)
            })
        |> runSync
    match result with
    | Ok true -> true
    | Ok v    -> failwith $"Unexpected Ok value: %A{v}"
    | Error e -> failwith $"Expected Ok but got Error: %A{e}"

[<Property(MaxTest = 1)>]
let ``T040b — LiveSession.refresh after session ends returns RenderFailed`` () =
    let c  = testConsole ()
    let ct = CancellationToken.None
    let mutable sess : LiveSession option = None
    let _ =
        Live.run c emptyComp ct (fun s ->
            async {
                sess <- Some s
                return Ok ()
            })
        |> runSync
    match sess with
    | None   -> failwith "Session was never captured"
    | Some s ->
        match LiveSession.refresh s with
        | Error (RenderError.RenderFailed ("LiveSession", _)) -> true
        | Error e -> failwith $"Expected RenderFailed(LiveSession) but got: %A{e}"
        | Ok _    -> failwith "Expected Error after session ended but got Ok"

// ============================================================================
// T041 — Status.run happy path
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T041a — Status.run body executes and returns Ok`` () =
    let c  = testConsole ()
    let ct = CancellationToken.None
    let result =
        Status.run c Spinner.Default (SafeText.ofLiteral "working...") ct (fun _ ->
            async { return Ok "status-ok" })
        |> runSync
    match result with
    | Ok "status-ok" -> true
    | Ok v           -> failwith $"Expected Ok \"status-ok\" but got Ok %A{v}"
    | Error e        -> failwith $"Expected Ok but got Error: %A{e}"

[<Property(MaxTest = 1)>]
let ``T041b — Status.run with pre-cancelled token returns UserCancelled`` () =
    let c   = testConsole ()
    let cts = new CancellationTokenSource ()
    cts.Cancel ()
    let result =
        Status.run c Spinner.Dots (SafeText.ofLiteral "x") cts.Token (fun _ ->
            async { return Ok () })
        |> runSync
    match result with
    | Error (RenderError.UserCancelled "status") -> true
    | Error e -> failwith $"Expected UserCancelled \"status\" but got Error: %A{e}"
    | Ok _    -> failwith "Expected Error but got Ok"

[<Property(MaxTest = 1)>]
let ``T041c — Status.run with Custom spinner (valid frames) returns Ok`` () =
    let c      = testConsole ()
    let ct     = CancellationToken.None
    let custom = Spinner.Custom (["/" ; "-" ; "\\" ; "|"], 80)
    let result =
        Status.run c custom (SafeText.ofLiteral "spinning") ct (fun _ ->
            async { return Ok true })
        |> runSync
    match result with
    | Ok true -> true
    | Ok v    -> failwith $"Unexpected Ok %A{v}"
    | Error e -> failwith $"Expected Ok but got Error: %A{e}"

[<Property(MaxTest = 1)>]
let ``T041d — Status.run with Custom spinner (empty frames) returns InvalidArgument`` () =
    let c      = testConsole ()
    let ct     = CancellationToken.None
    let custom = Spinner.Custom ([], 80)
    let result =
        Status.run c custom (SafeText.ofLiteral "x") ct (fun _ ->
            async { return Ok () })
        |> runSync
    match result with
    | Error (RenderError.InvalidArgument ("Spinner", _)) -> true
    | Error e -> failwith $"Expected InvalidArgument(Spinner) but got: %A{e}"
    | Ok _    -> failwith "Expected Error for empty frames but got Ok"

[<Property(MaxTest = 1)>]
let ``T041e — Status.run with Custom spinner (zero interval) returns InvalidArgument`` () =
    let c      = testConsole ()
    let ct     = CancellationToken.None
    let custom = Spinner.Custom (["x"], 0)
    let result =
        Status.run c custom (SafeText.ofLiteral "x") ct (fun _ ->
            async { return Ok () })
        |> runSync
    match result with
    | Error (RenderError.InvalidArgument ("Spinner", _)) -> true
    | Error e -> failwith $"Expected InvalidArgument(Spinner) but got: %A{e}"
    | Ok _    -> failwith "Expected Error for zero interval but got Ok"

// ============================================================================
// T046a — Spinner DU has 72 cases (71 named + Custom)
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T046a — Spinner DU has the expected known spinner cases`` () =
    // Verify a representative sample of named spinners exist (exhaustive match)
    // and resolve to Ok via the spinnerToSpectre path (indirectly via Status.run).
    // We test Default, Ascii, Dots, BetaWave, Aesthetic as representatives.
    let spinners = [
        Spinner.Default; Spinner.Ascii; Spinner.Dots
        Spinner.BetaWave; Spinner.Aesthetic; Spinner.BouncingBar
        Spinner.Christmas; Spinner.Moon
    ]
    let c  = testConsole ()
    let ct = CancellationToken.None
    spinners |> List.forall (fun spinner ->
        let result =
            Status.run c spinner (SafeText.ofLiteral "x") ct (fun _ ->
                async { return Ok () })
            |> runSync
        match result with
        | Ok ()   -> true
        | Error e -> failwith $"Spinner {spinner} failed: %A{e}")

// ============================================================================
// T042-T043 — StatusSession.updateMessage / updateSpinner
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T042 — StatusSession.updateMessage inside body returns Ok`` () =
    let c  = testConsole ()
    let ct = CancellationToken.None
    let result =
        Status.run c Spinner.Default (SafeText.ofLiteral "start") ct (fun sess ->
            async {
                let r = StatusSession.updateMessage sess (SafeText.ofLiteral "updated")
                return r |> Result.map (fun () -> true)
            })
        |> runSync
    match result with
    | Ok true -> true
    | Ok v    -> failwith $"Unexpected: %A{v}"
    | Error e -> failwith $"Expected Ok but got Error: %A{e}"

[<Property(MaxTest = 1)>]
let ``T043 — StatusSession.updateSpinner inside body returns Ok`` () =
    let c  = testConsole ()
    let ct = CancellationToken.None
    let result =
        Status.run c Spinner.Default (SafeText.ofLiteral "start") ct (fun sess ->
            async {
                let r = StatusSession.updateSpinner sess Spinner.Dots
                return r |> Result.map (fun () -> true)
            })
        |> runSync
    match result with
    | Ok true -> true
    | Ok v    -> failwith $"Unexpected: %A{v}"
    | Error e -> failwith $"Expected Ok but got Error: %A{e}"

// ============================================================================
// T044-T045 — Progress.run and ProgressSession.addTask
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T044 — Progress.run body executes and returns Ok`` () =
    let c  = testConsole ()
    let ct = CancellationToken.None
    let result =
        Progress.run c ct (fun _ -> async { return Ok "progress-ok" })
        |> runSync
    match result with
    | Ok "progress-ok" -> true
    | Ok v             -> failwith $"Expected Ok \"progress-ok\" but got Ok %A{v}"
    | Error e          -> failwith $"Expected Ok but got Error: %A{e}"

[<Property(MaxTest = 1)>]
let ``T044b — Progress.run with pre-cancelled token returns UserCancelled`` () =
    let c   = testConsole ()
    let cts = new CancellationTokenSource ()
    cts.Cancel ()
    let result =
        Progress.run c cts.Token (fun _ -> async { return Ok () })
        |> runSync
    match result with
    | Error (RenderError.UserCancelled "progress") -> true
    | Error e -> failwith $"Expected UserCancelled \"progress\" but got Error: %A{e}"
    | Ok _    -> failwith "Expected Error but got Ok"

[<Property(MaxTest = 1)>]
let ``T045a — ProgressSession.addTask returns a ProgressTask`` () =
    let c  = testConsole ()
    let ct = CancellationToken.None
    let result =
        Progress.run c ct (fun sess ->
            async {
                match ProgressSession.addTask sess (SafeText.ofLiteral "task1") with
                | Error e  -> return Error e
                | Ok task  -> return Ok (ProgressTask.maxValue task = 100.0)
            })
        |> runSync
    match result with
    | Ok true -> true
    | Ok v    -> failwith $"Unexpected: %A{v}"
    | Error e -> failwith $"Expected Ok but got Error: %A{e}"

[<Property(MaxTest = 1)>]
let ``T045b — ProgressTask.increment advances value`` () =
    let c  = testConsole ()
    let ct = CancellationToken.None
    let result =
        Progress.run c ct (fun sess ->
            async {
                match ProgressSession.addTask sess (SafeText.ofLiteral "task") with
                | Error e   -> return Error e
                | Ok task   ->
                    match ProgressTask.increment task 30.0 with
                    | Error e -> return Error e
                    | Ok ()   -> return Ok (ProgressTask.value task >= 30.0)
            })
        |> runSync
    match result with
    | Ok true -> true
    | Ok v    -> failwith $"Unexpected: %A{v}"
    | Error e -> failwith $"Expected Ok but got Error: %A{e}"

[<Property(MaxTest = 1)>]
let ``T045c — ProgressTask.setValue sets the task value`` () =
    let c  = testConsole ()
    let ct = CancellationToken.None
    let result =
        Progress.run c ct (fun sess ->
            async {
                match ProgressSession.addTask sess (SafeText.ofLiteral "task") with
                | Error e  -> return Error e
                | Ok task  ->
                    match ProgressTask.setValue task 50.0 with
                    | Error e -> return Error e
                    | Ok ()   -> return Ok (ProgressTask.value task >= 50.0)
            })
        |> runSync
    match result with
    | Ok true -> true
    | Ok v    -> failwith $"Unexpected: %A{v}"
    | Error e -> failwith $"Expected Ok but got Error: %A{e}"

[<Property(MaxTest = 1)>]
let ``T045d — ProgressTask.complete marks task as done`` () =
    let c  = testConsole ()
    let ct = CancellationToken.None
    let result =
        Progress.run c ct (fun sess ->
            async {
                match ProgressSession.addTask sess (SafeText.ofLiteral "task") with
                | Error e  -> return Error e
                | Ok task  ->
                    match ProgressTask.complete task with
                    | Error e -> return Error e
                    | Ok ()   -> return Ok (ProgressTask.value task = ProgressTask.maxValue task)
            })
        |> runSync
    match result with
    | Ok true -> true
    | Ok v    -> failwith $"Unexpected: %A{v}"
    | Error e -> failwith $"Expected Ok but got Error: %A{e}"

[<Property(MaxTest = 1)>]
let ``T045e — ProgressSession.addTask after session ends returns RenderFailed`` () =
    let c  = testConsole ()
    let ct = CancellationToken.None
    let mutable sess : ProgressSession option = None
    let _ =
        Progress.run c ct (fun s ->
            async {
                sess <- Some s
                return Ok ()
            })
        |> runSync
    match sess with
    | None   -> failwith "Session was never captured"
    | Some s ->
        match ProgressSession.addTask s (SafeText.ofLiteral "late") with
        | Error (RenderError.RenderFailed ("ProgressSession", _)) -> true
        | Error e -> failwith $"Expected RenderFailed(ProgressSession) but got: %A{e}"
        | Ok _    -> failwith "Expected Error after session ended but got Ok"
