module Fugue.Adapters.Console.Tests.RoundedMarkupTableTests

// see Helpers.fs FACT_DISCOVERY for why all tests are [<Property>]
open FsCheck.Xunit
open Fugue.Adapters.Console

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

let private ctx80 () =
    RenderContext.create 80 System.Int32.MaxValue true "default"
    |> function Ok c -> c | Error e -> failwith $"test ctx: {e}"

let private renderOk ctx comp =
    match Renderer.toRawAnsi ctx comp with
    | Ok s    -> s
    | Error e -> failwith $"Expected Ok but got Error: %A{e}"

// ============================================================================
// Happy path — 3-column table, 2 data rows
// ============================================================================

[<Property(MaxTest = 1)>]
let ``RoundedMarkupTable happy path: 3-column table renders non-empty with rounded borders and all labels`` () =
    let headers = [ ("Name", "left"); ("Pct", "right"); ("Status", "center") ]
    let rows    = [ [| "alice"; "42%"; "OK"   |]
                    [| "bob";   "17%"; "WARN" |] ]
    match RoundedMarkupTable.toComposition headers rows with
    | Error e -> failwith $"Expected Ok but got Error: %A{e}"
    | Ok comp ->
        let output = renderOk (ctx80 ()) comp
        // Non-empty
        output.Length > 0
        // Rounded border characters present
        && output.Contains "╭"
        && output.Contains "╰"
        // All header labels present
        && output.Contains "Name"
        && output.Contains "Pct"
        && output.Contains "Status"
        // Data row values present
        && output.Contains "alice"
        && output.Contains "bob"
        && output.Contains "42%"
        && output.Contains "WARN"

// ============================================================================
// Edge — empty rows (header-only render)
// ============================================================================

[<Property(MaxTest = 1)>]
let ``RoundedMarkupTable empty rows: headers rendered without crashing, no data rows`` () =
    let headers = [ ("Name", "left"); ("Pct", "right"); ("Status", "center") ]
    let rows : string[] list = []
    match RoundedMarkupTable.toComposition headers rows with
    | Error e -> failwith $"Expected Ok but got Error: %A{e}"
    | Ok comp ->
        let output = renderOk (ctx80 ()) comp
        // Headers still visible
        output.Contains "Name"
        && output.Contains "Pct"
        && output.Contains "Status"
        // No data values
        && not (output.Contains "alice")

// ============================================================================
// Edge — zero columns (empty headers + empty rows)
// ============================================================================

[<Property(MaxTest = 1)>]
let ``RoundedMarkupTable zero columns: returns Ok with empty output or renders without crashing`` () =
    let headers : (string * string) list = []
    let rows : string[] list = []
    // With zero columns the call should succeed (no alignment strings to validate)
    // and produce output (possibly empty or a minimal border).
    match RoundedMarkupTable.toComposition headers rows with
    | Error e -> failwith $"Expected Ok for zero-column table but got Error: %A{e}"
    | Ok _    -> true   // render succeeds without crashing

// ============================================================================
// Edge — empty cell strings
// ============================================================================

[<Property(MaxTest = 1)>]
let ``RoundedMarkupTable empty cell: renders without crashing and produces non-empty output`` () =
    let headers = [ ("Col1", "left"); ("Col2", "right") ]
    let rows    = [ [| "value"; "" |] ]   // second cell deliberately empty
    match RoundedMarkupTable.toComposition headers rows with
    | Error e -> failwith $"Expected Ok but got Error: %A{e}"
    | Ok comp ->
        let output = renderOk (ctx80 ()) comp
        output.Length > 0
        && output.Contains "Col1"
        && output.Contains "value"

// ============================================================================
// Error path — invalid alignment string returns Error InvalidArgument
// ============================================================================

[<Property(MaxTest = 1)>]
let ``RoundedMarkupTable invalid alignment: returns Error InvalidArgument`` () =
    let headers = [ ("Name", "left"); ("Pct", "justify") ]   // "justify" is not a valid alignment
    let rows    = [ [| "alice"; "42%" |] ]
    match RoundedMarkupTable.toComposition headers rows with
    | Error (RenderError.InvalidArgument ("RoundedMarkupTable", detail)) ->
        detail.Contains "justify"
    | Error e -> failwith $"Expected InvalidArgument(RoundedMarkupTable) but got: %A{e}"
    | Ok _    -> failwith "Expected Error for unknown alignment but got Ok"
