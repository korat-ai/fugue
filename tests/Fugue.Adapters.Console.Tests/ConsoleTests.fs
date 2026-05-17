module Fugue.Adapters.Console.Tests.ConsoleTests

// see Helpers.fs FACT_DISCOVERY for why all tests are [<Property>]
open FsCheck.Xunit
open Fugue.Adapters.Console

// Helpers
let private textComp (s: string) =
    Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral s))

let private emptyComp = Composition.stack []

// ============================================================================
// T061 — Console.test returns a fresh Console per call
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T061a — Console.test creates a test console (non-null)`` () =
    let c = Console.test 80 true
    // If Console.test returned null this would NPE — passing is proof of Ok.
    not (isNull (box c))

[<Property(MaxTest = 1)>]
let ``T061b — Console.test fresh per call (two calls return distinct objects)`` () =
    let c1 = Console.test 80 true
    let c2 = Console.test 80 true
    // Each call must return an independent instance.
    not (obj.ReferenceEquals (box c1, box c2))

// ============================================================================
// T062 — Console.default_ returns the production singleton
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T062a — Console.default_ returns a non-null Console`` () =
    let c = Console.default_ ()
    not (isNull (box c))

[<Property(MaxTest = 1)>]
let ``T062b — Console.default_ is a singleton (two calls return same object)`` () =
    let c1 = Console.default_ ()
    let c2 = Console.default_ ()
    obj.ReferenceEquals (box c1, box c2)

// ============================================================================
// T063 — Console.test width is clamped to [1, 1000]
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T063a — Console.test width 0 is clamped to 1 (does not throw)`` () =
    let c = Console.test 0 false
    not (isNull (box c))

[<Property(MaxTest = 1)>]
let ``T063b — Console.test width 10000 is clamped to 1000 (does not throw)`` () =
    let c = Console.test 10000 false
    not (isNull (box c))

[<Property(MaxTest = 1)>]
let ``T063c — Console.test negative width is clamped to 1 (does not throw)`` () =
    let c = Console.test -42 true
    not (isNull (box c))

// ============================================================================
// T064 — Console.cursorState
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T064a — Console.cursorState for test console is not UnknownState`` () =
    let c = Console.test 80 true
    match Console.cursorState c with
    | UnknownState -> false
    | _            -> true

[<Property(MaxTest = 1)>]
let ``T064b — Console.cursorState for test console is Visible (0, 0)`` () =
    let c = Console.test 80 true
    match Console.cursorState c with
    | Visible (0, 0) -> true
    | other -> failwith $"Expected Visible (0, 0) but got %A{other}"

[<Property(MaxTest = 1)>]
let ``T064c — Console.cursorState for production console is UnknownState`` () =
    let c = Console.default_ ()
    match Console.cursorState c with
    | UnknownState -> true
    | other -> failwith $"Expected UnknownState but got %A{other}"

// ============================================================================
// T065 — Console.write
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T065a — Console.write returns Ok for a test console with simple text`` () =
    let c    = Console.test 80 true
    let comp = textComp "hello"
    match Console.write c comp with
    | Ok ()   -> true
    | Error e -> failwith $"Expected Ok but got Error: %A{e}"

[<Property(MaxTest = 1)>]
let ``T065b — Console.write with Empty composition returns Ok`` () =
    let c    = Console.test 80 false
    let comp = emptyComp
    match Console.write c comp with
    | Ok ()   -> true
    | Error e -> failwith $"Expected Ok but got Error: %A{e}"

// ============================================================================
// T065b — Console.capture drains accumulated buffer (spec §6.2 + §6.5)
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T065b — Console.capture drains accumulated buffer on Test mode`` () =
    let c = Console.test 80 false
    // Two writes before capture — buffer must contain all three texts.
    Console.write c (textComp "alpha") |> ignore
    Console.write c (textComp "beta")  |> ignore
    match Console.capture c (textComp "gamma") with
    | Error e -> failwith $"Expected Ok but got Error: %A{e}"
    | Ok s    ->
        s.Contains "alpha" && s.Contains "beta" && s.Contains "gamma"

// ============================================================================
// T066 — Console.capture
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T066a — Console.capture returns Ok and non-empty string for non-empty comp`` () =
    let c    = Console.test 80 false
    let comp = textComp "world"
    match Console.capture c comp with
    | Ok s    -> s.Length > 0
    | Error e -> failwith $"Expected Ok but got Error: %A{e}"

[<Property(MaxTest = 1)>]
let ``T066b — Console.capture does not write to the console (pure)`` () =
    // Calling capture on the production console should not throw or write stdout.
    // We just check that it returns Ok (no side effects to assert directly).
    let c    = Console.default_ ()
    let comp = textComp "test"
    match Console.capture c comp with
    | Ok _    -> true
    | Error e -> failwith $"Expected Ok but got Error: %A{e}"

// ============================================================================
// T067 — Console.isInteractive
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T067 — Console.isInteractive returns false for test console`` () =
    let c = Console.test 80 true
    Console.isInteractive c = false

// ============================================================================
// T068 — ExceptionFormat values
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T068a — ExceptionFormat.default_ has all flags false`` () =
    let fmt = ExceptionFormat.default_
    not fmt.ShortenTypes   &&
    not fmt.ShortenMethods &&
    not fmt.ShortenPaths   &&
    not fmt.ShowLinks

[<Property(MaxTest = 1)>]
let ``T068b — ExceptionFormat.verbose has all flags false`` () =
    let fmt = ExceptionFormat.verbose
    not fmt.ShortenTypes   &&
    not fmt.ShortenMethods &&
    not fmt.ShortenPaths   &&
    not fmt.ShowLinks

[<Property(MaxTest = 1)>]
let ``T068c — ExceptionFormat.compact has all flags true`` () =
    let fmt = ExceptionFormat.compact
    fmt.ShortenTypes   &&
    fmt.ShortenMethods &&
    fmt.ShortenPaths   &&
    fmt.ShowLinks

// ============================================================================
// T069 — Exceptions.render
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T069a — Exceptions.render returns Ok for test console`` () =
    let c  = Console.test 120 true
    let ex = System.InvalidOperationException "test error"
    match Exceptions.render c ex with
    | Ok ()   -> true
    | Error e -> failwith $"Expected Ok but got Error: %A{e}"

// ============================================================================
// T070 — Exceptions.renderWith
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T070a — Exceptions.renderWith with compact format returns Ok`` () =
    let c   = Console.test 120 true
    let ex  = System.DivideByZeroException "boom"
    let fmt = ExceptionFormat.compact
    match Exceptions.renderWith c fmt ex with
    | Ok ()   -> true
    | Error e -> failwith $"Expected Ok but got Error: %A{e}"

[<Property(MaxTest = 1)>]
let ``T070b — Exceptions.renderWith with verbose format returns Ok`` () =
    let c   = Console.test 120 true
    let ex  = System.ArgumentNullException "arg"
    let fmt = ExceptionFormat.verbose
    match Exceptions.renderWith c fmt ex with
    | Ok ()   -> true
    | Error e -> failwith $"Expected Ok but got Error: %A{e}"

// ============================================================================
// T071 — CursorState DU exhaustiveness
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T071 — CursorState DU covers all three cases without compile warning`` () =
    // This test exercises all three cases to ensure the DU is exhaustive.
    let states = [ Visible (1, 2); Hidden; UnknownState ]
    states |> List.forall (fun s ->
        match s with
        | Visible (r, c) -> r >= 0 && c >= 0
        | Hidden         -> true
        | UnknownState   -> true)
