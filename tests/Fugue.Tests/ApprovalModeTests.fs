module Fugue.Tests.ApprovalModeTests

/// Unit tests for `Fugue.Core.ApprovalMode`.
///
/// Covers:
///   - cycle order (Shift+Tab progression) is exactly one full loop
///   - icon / label string format
///   - tryParse round-trips with `label` for all modes
///   - tryParse is case-insensitive and accepts both `auto-edit` and `autoedit`
///   - tryParse returns None on garbage input (no exceptions)
///   - requiresApproval matrix matches the documented table

open Xunit
open FsUnit.Xunit
open Fugue.Core

[<Fact>]
let ``cycle visits all four modes and returns to start in 4 steps`` () =
    let start = ApprovalMode.Plan
    let next1 = ApprovalMode.cycle start
    let next2 = ApprovalMode.cycle next1
    let next3 = ApprovalMode.cycle next2
    let next4 = ApprovalMode.cycle next3
    next1 |> should equal ApprovalMode.Default
    next2 |> should equal ApprovalMode.AutoEdit
    next3 |> should equal ApprovalMode.YOLO
    next4 |> should equal ApprovalMode.Plan  // back to start

[<Fact>]
let ``label returns lowercase short name without glyph`` () =
    ApprovalMode.label ApprovalMode.Plan     |> should equal "plan"
    ApprovalMode.label ApprovalMode.Default  |> should equal "default"
    ApprovalMode.label ApprovalMode.AutoEdit |> should equal "auto-edit"
    ApprovalMode.label ApprovalMode.YOLO     |> should equal "yolo"

[<Fact>]
let ``icon contains label and a single-glyph prefix`` () =
    let pairs =
        [ ApprovalMode.Plan;      ApprovalMode.Default
          ApprovalMode.AutoEdit;  ApprovalMode.YOLO ]
        |> List.map (fun m -> m, ApprovalMode.icon m)
    for mode, ic in pairs do
        ic |> should haveSubstring (ApprovalMode.label mode)
        // First "word" is the glyph; second segment is the label.
        let parts = ic.Split ' '
        parts.Length |> should equal 2

[<Fact>]
let ``tryParse round-trips every mode via label`` () =
    let allModes =
        [ ApprovalMode.Plan; ApprovalMode.Default
          ApprovalMode.AutoEdit; ApprovalMode.YOLO ]
    for m in allModes do
        let parsed = ApprovalMode.tryParse (ApprovalMode.label m)
        parsed |> should equal (Some m)

[<Fact>]
let ``tryParse accepts uppercase and surrounding whitespace`` () =
    ApprovalMode.tryParse "  PLAN  "      |> should equal (Some ApprovalMode.Plan)
    ApprovalMode.tryParse "Default"       |> should equal (Some ApprovalMode.Default)
    ApprovalMode.tryParse "AUTO-EDIT"     |> should equal (Some ApprovalMode.AutoEdit)
    ApprovalMode.tryParse "yOlO"          |> should equal (Some ApprovalMode.YOLO)

[<Fact>]
let ``tryParse accepts both autoedit and auto-edit forms`` () =
    ApprovalMode.tryParse "auto-edit" |> should equal (Some ApprovalMode.AutoEdit)
    ApprovalMode.tryParse "autoedit"  |> should equal (Some ApprovalMode.AutoEdit)

[<Fact>]
let ``tryParse returns None for unknown input without throwing`` () =
    ApprovalMode.tryParse ""           |> should equal None
    ApprovalMode.tryParse "safe"       |> should equal None
    ApprovalMode.tryParse "auto"       |> should equal None
    ApprovalMode.tryParse null         |> should equal None  // null → empty → None

[<Fact>]
let ``requiresApproval YOLO is false for everything`` () =
    for tool in ["read"; "edit"; "bash"; "anything"] do
        ApprovalMode.requiresApproval ApprovalMode.YOLO tool |> should equal false

[<Fact>]
let ``requiresApproval Plan is true for everything`` () =
    for tool in ["read"; "edit"; "bash"; "anything"] do
        ApprovalMode.requiresApproval ApprovalMode.Plan tool |> should equal true

[<Fact>]
let ``requiresApproval AutoEdit prompts only for bash and unknown`` () =
    ApprovalMode.requiresApproval ApprovalMode.AutoEdit "read"     |> should equal false
    ApprovalMode.requiresApproval ApprovalMode.AutoEdit "edit"     |> should equal false
    ApprovalMode.requiresApproval ApprovalMode.AutoEdit "bash"     |> should equal true
    ApprovalMode.requiresApproval ApprovalMode.AutoEdit "unknown"  |> should equal true

[<Fact>]
let ``requiresApproval Default skips read but prompts edit and bash`` () =
    ApprovalMode.requiresApproval ApprovalMode.Default "read" |> should equal false
    ApprovalMode.requiresApproval ApprovalMode.Default "edit" |> should equal true
    ApprovalMode.requiresApproval ApprovalMode.Default "bash" |> should equal true
