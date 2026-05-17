namespace Fugue.Adapters.Console

// =============================================================================
// Prompts — Phase 2 (US4)
//
// Async F# wrappers over Spectre's four interactive prompt types.
// Implementation per data-model.md §5 and research.md §R3.
//
// T056: projectLabel + projectTitle private helpers
// T057: TextPrompt.ask
// T058: SelectionPrompt.ask
// T059: MultiSelectionPrompt.ask
// T060: ConfirmationPrompt.ask
// =============================================================================

// Validator<'T> is declared in Prompts.fsi as a type alias.
// The .fs must provide a compatible definition for use within this file.
// F# type aliases in .fsi are opaque to the implementation module unless
// re-declared here as well.
type Validator<'T> = string -> Result<'T, string>

// ─────────────────────────────────────────────────────────────────────────────
// Private implementation helpers
// ─────────────────────────────────────────────────────────────────────────────

module private PromptHelpers =

    // T056: Project a Composition label to a plain-text string (colour stripped).
    //
    // Per data-model.md §0.1: forks the RenderContext with ColourEnabled = false,
    // calls Renderer.toRawAnsi, returns the string or empty on error.
    // The Converter callback cannot itself return Result, so render errors are
    // silently swallowed here; pre-flight validation (below) catches them first.
    let projectLabel (comp: Composition) : string =
        // Build a colour-disabled RenderContext at a fixed 80-column width.
        // Width 80 is wide enough for most labels; layout inside the list item
        // is controlled by Spectre, not by us.
        let ctx = RenderContext.create 80 false "default"
        match Renderer.toRawAnsi ctx comp with
        | Ok s    -> s
        | Error _ -> ""

    // T056: Project a Composition title to a string for the prompt title.
    // Returns Result so callers can bail out before entering ShowAsync.
    // Unlike projectLabel, the title retains colour (no forced strip).
    let projectTitle (console: Console) (comp: Composition) : Result<string, RenderError> =
        Console.capture console comp

    // Safely extract a message from a possibly-null exception.
    // Under .NET 10 strict nullability, catch binds ex: exn | null.
    let exnMessage (ex: exn | null) : string =
        match ex with
        | null    -> "(null exception)"
        | nonNull -> nonNull.Message

    // Pre-flight all Composition labels in a choices list.
    // Returns a ('T * string) list on success (parallel list of projected labels),
    // or Error on first failure.
    // Using a plain list avoids Dictionary's 'T: not null + 'T: equality constraints,
    // which would propagate into the public ask signatures and complicate caller code.
    let preflightLabels
            (promptName: string)
            (choices: ('T * Composition) list)
            : Result<('T * string) list, RenderError> =
        let mutable firstError : RenderError option = None
        let mutable i = 0
        let projected = ResizeArray<'T * string> ()
        for (value, labelComp) in choices do
            if firstError.IsNone then
                let label = projectLabel labelComp
                if label = "" then
                    // Empty projection means projectLabel silently swallowed a render error.
                    // Surface it as an InvalidArgument pre-flight failure.
                    firstError <- Some (RenderError.InvalidArgument (promptName, $"choice {i} label failed to render"))
                else
                    projected.Add (value, label)
            i <- i + 1
        match firstError with
        | Some e -> Error e
        | None   -> Ok (projected |> Seq.toList)

// ─────────────────────────────────────────────────────────────────────────────
// T057: TextPrompt.ask
// ─────────────────────────────────────────────────────────────────────────────

module TextPrompt =

    let ask
            (console: Console)
            (title: Composition)
            (validator: Validator<'T>)
            (cancellation: System.Threading.CancellationToken)
            : Async<Result<'T, RenderError>> =
        async {
            // Non-TTY short-circuit (FR-016, §5.7): check before any blocking IO.
            if not (Console.isInteractive console) then
                return Error (RenderError.NoInteractiveConsole "text-prompt")
            // Pre-cancellation check (defensive; ShowAsync will also honour it).
            elif cancellation.IsCancellationRequested then
                return Error (RenderError.UserCancelled "text-prompt")
            else

            // Project the Composition title to a string.
            match PromptHelpers.projectTitle console title with
            | Error e ->
                return Error e
            | Ok titleStr ->

            let backend = unbox<Spectre.Console.IAnsiConsole> (ConsoleAccess.backend console)

            // Build Spectre TextPrompt<string> internally.
            // We always use TextPrompt<string> so we can run the typed validator
            // twice: once inside Spectre's re-prompt loop, once after ShowAsync
            // returns to lift the raw string to 'T (data-model.md §5.2, research.md §R3).
            let prompt = Spectre.Console.TextPrompt<string> (titleStr)

            // Wire the F# validator into Spectre's Validate hook.
            // Spectre calls this inside its re-prompt loop; returning
            // ValidationResult.Error triggers inline re-prompt with our message.
            prompt.Validator <-
                System.Func<string, Spectre.Console.ValidationResult> (
                    fun input ->
                        match validator input with
                        | Ok _      -> Spectre.Console.ValidationResult.Success ()
                        | Error msg -> Spectre.Console.ValidationResult.Error msg)

            try
                // ShowAsync(IAnsiConsole, CancellationToken) — verified via reflection.
                let! rawString =
                    prompt.ShowAsync (backend, cancellation)
                    |> Async.AwaitTask

                // Second validator invocation to lift string -> 'T.
                match validator rawString with
                | Ok typed  -> return Ok typed
                | Error msg -> return Error (RenderError.RenderFailed ("TextPrompt", $"validator failed after ShowAsync: {msg}"))
            with
            // TaskCanceledException inherits OperationCanceledException — one arm covers both.
            | :? System.OperationCanceledException ->
                return Error (RenderError.UserCancelled "text-prompt")
            | ex ->
                return Error (RenderError.RenderFailed ("TextPrompt", PromptHelpers.exnMessage ex))
        }

// ─────────────────────────────────────────────────────────────────────────────
// T058: SelectionPrompt.ask
// ─────────────────────────────────────────────────────────────────────────────

module SelectionPrompt =

    let ask
            (console: Console)
            (title: Composition)
            (choices: ('T * Composition) list)
            (cancellation: System.Threading.CancellationToken)
            : Async<Result<'T, RenderError>> =
        async {
            // Non-TTY short-circuit (FR-016).
            if not (Console.isInteractive console) then
                return Error (RenderError.NoInteractiveConsole "selection")
            // Empty choices pre-flight (§5.3): synchronous, before ShowAsync.
            elif choices.IsEmpty then
                return Error (RenderError.EmptyComposition "SelectionPrompt: at least one choice required")
            // Pre-cancellation check.
            elif cancellation.IsCancellationRequested then
                return Error (RenderError.UserCancelled "selection")
            else

            // Pre-flight all labels: build parallel (value, projectedLabel) list.
            match PromptHelpers.preflightLabels "SelectionPrompt" choices with
            | Error e -> return Error e
            | Ok labelPairs ->

            // Project the title.
            match PromptHelpers.projectTitle console title with
            | Error e -> return Error e
            | Ok titleStr ->

            let backend = unbox<Spectre.Console.IAnsiConsole> (ConsoleAccess.backend console)

            // Build SelectionPrompt<'T> internally.
            let prompt = Spectre.Console.SelectionPrompt<'T> ()
            prompt.Title <- titleStr

            // Wire Converter so Spectre displays our projected plain-text labels.
            // We build a lookup from the parallel label list. Since Converter is called
            // with the exact value instances we added via AddChoice, we use reference
            // equality via System.Object.ReferenceEquals for object types. However, F#
            // value types use structural equality. To handle both uniformly we scan the
            // list linearly — the list is short (prompt choices) so O(n) is fine.
            // Converter: Func<'T, string> — verified via reflection.
            let findLabel (v: 'T) =
                labelPairs
                |> List.tryFind (fun (k, _) -> System.Object.Equals (box k, box v))
                |> Option.map snd
                |> Option.defaultValue (string v)   // fallback: use ToString if lookup misses
            prompt.Converter <- System.Func<'T, string> findLabel

            // Add all choice values.
            for (value, _) in labelPairs do
                prompt.AddChoice value |> ignore

            try
                let! selected =
                    prompt.ShowAsync (backend, cancellation)
                    |> Async.AwaitTask
                return Ok selected
            with
            // TaskCanceledException inherits OperationCanceledException — one arm covers both.
            | :? System.OperationCanceledException ->
                return Error (RenderError.UserCancelled "selection")
            | ex ->
                return Error (RenderError.RenderFailed ("SelectionPrompt", PromptHelpers.exnMessage ex))
        }

// ─────────────────────────────────────────────────────────────────────────────
// T059: MultiSelectionPrompt.ask
// ─────────────────────────────────────────────────────────────────────────────

module MultiSelectionPrompt =

    let ask
            (console: Console)
            (title: Composition)
            (choices: ('T * Composition) list)
            (cancellation: System.Threading.CancellationToken)
            : Async<Result<'T list, RenderError>> =
        async {
            // Non-TTY short-circuit (FR-016).
            if not (Console.isInteractive console) then
                return Error (RenderError.NoInteractiveConsole "multi-selection")
            // Empty choices pre-flight: synchronous, before ShowAsync.
            elif choices.IsEmpty then
                return Error (RenderError.EmptyComposition "MultiSelectionPrompt: at least one choice required")
            // Pre-cancellation check.
            elif cancellation.IsCancellationRequested then
                return Error (RenderError.UserCancelled "multi-selection")
            else

            // Pre-flight all labels: build parallel (value, projectedLabel) list.
            match PromptHelpers.preflightLabels "MultiSelectionPrompt" choices with
            | Error e -> return Error e
            | Ok labelPairs ->

            // Project the title.
            match PromptHelpers.projectTitle console title with
            | Error e -> return Error e
            | Ok titleStr ->

            let backend = unbox<Spectre.Console.IAnsiConsole> (ConsoleAccess.backend console)

            // Build MultiSelectionPrompt<'T> internally.
            let prompt = Spectre.Console.MultiSelectionPrompt<'T> ()
            prompt.Title <- titleStr

            // Wire Converter so Spectre displays our projected plain-text labels.
            let findLabel (v: 'T) =
                labelPairs
                |> List.tryFind (fun (k, _) -> System.Object.Equals (box k, box v))
                |> Option.map snd
                |> Option.defaultValue (string v)
            prompt.Converter <- System.Func<'T, string> findLabel

            // Add all choice values.
            for (value, _) in labelPairs do
                prompt.AddChoice value |> ignore

            try
                // ShowAsync returns Task<List<'T>> — verified via reflection.
                let! selectedList =
                    prompt.ShowAsync (backend, cancellation)
                    |> Async.AwaitTask

                // Convert System.Collections.Generic.List<'T> to F# list.
                // Empty selection (user confirmed with nothing checked) → Ok [] per §5.4.
                return Ok (selectedList |> Seq.toList)
            with
            // TaskCanceledException inherits OperationCanceledException — one arm covers both.
            | :? System.OperationCanceledException ->
                return Error (RenderError.UserCancelled "multi-selection")
            | ex ->
                return Error (RenderError.RenderFailed ("MultiSelectionPrompt", PromptHelpers.exnMessage ex))
        }

// ─────────────────────────────────────────────────────────────────────────────
// T060: ConfirmationPrompt.ask
// ─────────────────────────────────────────────────────────────────────────────

module ConfirmationPrompt =

    let ask
            (console: Console)
            (title: Composition)
            (defaultChoice: bool)
            (cancellation: System.Threading.CancellationToken)
            : Async<Result<bool, RenderError>> =
        async {
            // Non-TTY short-circuit (FR-016).
            if not (Console.isInteractive console) then
                return Error (RenderError.NoInteractiveConsole "confirmation")
            // Pre-cancellation check.
            elif cancellation.IsCancellationRequested then
                return Error (RenderError.UserCancelled "confirmation")
            else

            // Project the title.
            match PromptHelpers.projectTitle console title with
            | Error e -> return Error e
            | Ok titleStr ->

            let backend = unbox<Spectre.Console.IAnsiConsole> (ConsoleAccess.backend console)

            // Build ConfirmationPrompt internally.
            let prompt = Spectre.Console.ConfirmationPrompt (titleStr)
            prompt.DefaultValue <- defaultChoice

            try
                // ShowAsync returns Task<bool> — verified via reflection.
                let! answer =
                    prompt.ShowAsync (backend, cancellation)
                    |> Async.AwaitTask
                return Ok answer
            with
            // TaskCanceledException inherits OperationCanceledException — one arm covers both.
            | :? System.OperationCanceledException ->
                return Error (RenderError.UserCancelled "confirmation")
            | ex ->
                return Error (RenderError.RenderFailed ("ConfirmationPrompt", PromptHelpers.exnMessage ex))
        }
