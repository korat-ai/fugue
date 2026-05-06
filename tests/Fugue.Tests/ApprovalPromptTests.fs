module Fugue.Tests.ApprovalPromptTests

/// Tests for the approval-prompt gate. We exercise:
///   - tool-name → kind mapping
///   - buildGate skips the prompt when the mode doesn't require approval
///   - buildGate denies in non-interactive mode when approval is required
///     (fail closed — no UI to ask)
///   - buildGate reads the *current* mode each call (Shift+Tab cycle is honored)
///
/// The interactive prompt itself is NOT tested here — it reads from
/// Console.ReadKey, which can't be driven from xUnit. Manual TTY validation
/// is the only honest way to exercise that path.

open System
open Xunit
open FsUnit.Xunit
open Fugue.Core
open Fugue.Cli.ApprovalPrompt

let private run (a: Async<bool>) : bool = Async.RunSynchronously a

[<Fact>]
let ``toolKind maps Read tools to read`` () =
    toolKind "Read" |> should equal "read"
    toolKind "Glob" |> should equal "read"
    toolKind "Grep" |> should equal "read"
    toolKind "Tree" |> should equal "read"
    toolKind "GetConversation" |> should equal "read"

[<Fact>]
let ``toolKind maps mutating tools to edit`` () =
    toolKind "Write"      |> should equal "edit"
    toolKind "WriteBatch" |> should equal "edit"
    toolKind "Edit"       |> should equal "edit"

[<Fact>]
let ``toolKind maps Bash to bash`` () =
    toolKind "Bash" |> should equal "bash"

[<Fact>]
let ``toolKind defaults unknown tools to edit (fail safer)`` () =
    toolKind "MysteryTool"  |> should equal "edit"
    toolKind ""             |> should equal "edit"

[<Fact>]
let ``buildGate skips prompt for read tools in Default mode`` () =
    let mutable mode = ApprovalMode.Default
    let gate = buildGate (fun () -> mode) false  // non-interactive
    gate "Read" "{}" |> run |> should equal true

[<Fact>]
let ``buildGate denies in non-interactive when approval required`` () =
    let gate = buildGate (fun () -> ApprovalMode.Default) false
    // Default mode requires approval for "edit" and "bash"
    gate "Write" "{}" |> run |> should equal false
    gate "Bash"  "{}" |> run |> should equal false

[<Fact>]
let ``buildGate allows everything in YOLO regardless of interactive`` () =
    let gateInt = buildGate (fun () -> ApprovalMode.YOLO) true
    let gateNoInt = buildGate (fun () -> ApprovalMode.YOLO) false
    for tool in ["Read"; "Write"; "Bash"; "Edit"] do
        gateInt   tool "{}" |> run |> should equal true
        gateNoInt tool "{}" |> run |> should equal true

[<Fact>]
let ``buildGate denies everything except read in Plan mode (non-interactive)`` () =
    let gate = buildGate (fun () -> ApprovalMode.Plan) false
    // Plan requires approval for ALL kinds — including read
    gate "Read"  "{}" |> run |> should equal false
    gate "Write" "{}" |> run |> should equal false
    gate "Bash"  "{}" |> run |> should equal false

[<Fact>]
let ``buildGate honors live mode cycling via getMode closure`` () =
    let mutable mode = ApprovalMode.YOLO
    let gate = buildGate (fun () -> mode) false
    // YOLO → allow
    gate "Bash" "{}" |> run |> should equal true
    // Cycle to Plan → deny
    mode <- ApprovalMode.Plan
    gate "Bash" "{}" |> run |> should equal false
    // Cycle to AutoEdit → deny bash, allow read/edit
    mode <- ApprovalMode.AutoEdit
    gate "Bash"  "{}" |> run |> should equal false
    gate "Write" "{}" |> run |> should equal true
    gate "Read"  "{}" |> run |> should equal true
