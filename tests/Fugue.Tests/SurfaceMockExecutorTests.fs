module Fugue.Tests.SurfaceMockExecutorTests

open Xunit
open FsUnit.Xunit
open Fugue.Surface

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

let private mkTerm () = MockExecutor.create 40 10

// ---------------------------------------------------------------------------
// MockExecutor: paint StatusBarLine1 writes to correct row
// ---------------------------------------------------------------------------
[<Fact>]
let ``paint StatusBarLine1 writes to row height-2`` () =
    let term = mkTerm ()
    let ops  = surface { yield paint Region.StatusBarLine1 "hello" Style.normal }
    MockExecutor.execute ops term |> ignore
    // height=10, StatusBarLine1 = row (10-2) = 8
    MockExecutor.lineAt 8 term |> should equal "hello"

// ---------------------------------------------------------------------------
// MockExecutor: paint StatusBarLine2 writes to correct row
// ---------------------------------------------------------------------------
[<Fact>]
let ``paint StatusBarLine2 writes to row height-1`` () =
    let term = mkTerm ()
    let ops  = surface { yield paint Region.StatusBarLine2 "status" Style.normal }
    MockExecutor.execute ops term |> ignore
    MockExecutor.lineAt 9 term |> should equal "status"

// ---------------------------------------------------------------------------
// MockExecutor: regionContent returns correct line
// ---------------------------------------------------------------------------
[<Fact>]
let ``regionContent resolves StatusBarLine1 to matching line`` () =
    let term = mkTerm ()
    let ops  = surface { yield paint Region.StatusBarLine1 "cwd: /home" Style.normal }
    MockExecutor.execute ops term |> ignore
    MockExecutor.regionContent Region.StatusBarLine1 term |> should equal "cwd: /home"

// ---------------------------------------------------------------------------
// MockExecutor: eraseLine clears a line back to empty
// ---------------------------------------------------------------------------
[<Fact>]
let ``eraseLine clears a previously painted line`` () =
    let term = mkTerm ()
    MockExecutor.execute (surface { yield paint Region.StatusBarLine2 "was here" Style.normal }) term |> ignore
    MockExecutor.execute (surface { yield eraseLine Region.StatusBarLine2 }) term |> ignore
    MockExecutor.lineAt 9 term |> should equal ""

// ---------------------------------------------------------------------------
// MockExecutor: moveTo updates cursor position
// ---------------------------------------------------------------------------
[<Fact>]
let ``moveTo updates CursorRow and CursorCol`` () =
    let term = mkTerm ()
    MockExecutor.execute [ DrawOp.MoveTo(Region.StatusBarLine1, 5) ] term |> ignore
    term.CursorCol |> should equal 5
    term.CursorRow |> should equal 8   // height-2 = 8

// ---------------------------------------------------------------------------
// MockExecutor: Append adds text at cursor row and advances row
// ---------------------------------------------------------------------------
[<Fact>]
let ``Append writes text at cursor row and advances to next row`` () =
    let term = mkTerm ()
    term.CursorRow <- 0
    MockExecutor.execute [ DrawOp.Append "first line" ] term |> ignore
    MockExecutor.lineAt 0 term |> should equal "first line"
    term.CursorRow |> should equal 1

// ---------------------------------------------------------------------------
// MockExecutor: sequential ops apply in order — paint then erase = empty
// ---------------------------------------------------------------------------
[<Fact>]
let ``sequential paint then eraseLine leaves line empty`` () =
    let term = mkTerm ()
    let ops =
        surface {
            yield paint    Region.ThinkingLine "transient" Style.normal
            yield eraseLine Region.ThinkingLine
        }
    MockExecutor.execute ops term |> ignore
    MockExecutor.regionContent Region.ThinkingLine term |> should equal ""

// ---------------------------------------------------------------------------
// MockExecutor: WriteLog records all applied ops
// ---------------------------------------------------------------------------
[<Fact>]
let ``WriteLog accumulates all applied DrawOps`` () =
    let term = mkTerm ()
    let ops  = surface { yield paint Region.StatusBarLine1 "a" Style.normal; yield eraseLine Region.StatusBarLine2 }
    MockExecutor.execute ops term |> ignore
    term.WriteLog |> List.length |> should equal 2

// ---------------------------------------------------------------------------
// MockExecutor: out-of-bounds InputArea row clamps and does not crash
// ---------------------------------------------------------------------------
[<Fact>]
let ``paint with InputArea row beyond terminal height clamps and does not crash`` () =
    // terminal height=10; InputArea with large row → clamped to row 0
    let term = MockExecutor.create 40 10
    let ops  = [ DrawOp.Paint(Region.InputArea 1000, "safe", Style.normal) ]
    // Should not throw
    MockExecutor.execute ops term |> ignore
    term.WriteLog |> List.length |> should equal 1

// ---------------------------------------------------------------------------
// Scenario: Render status bar with streaming spinner
// ---------------------------------------------------------------------------
[<Fact>]
let ``status bar render fills StatusBarLine1, StatusBarLine2 and ThinkingLine`` () =
    let term = MockExecutor.create 40 10
    let ops =
        surface {
            yield paint Region.StatusBarLine1 "/workspace"  Style.normal
            yield paint Region.StatusBarLine2 "claude-opus" Style.normal
            yield paint Region.ThinkingLine   "thinking..."   Style.normal
        }
    MockExecutor.execute ops term |> ignore
    MockExecutor.regionContent Region.StatusBarLine1 term |> should equal "/workspace"
    MockExecutor.regionContent Region.StatusBarLine2 term |> should equal "claude-opus"
    MockExecutor.regionContent Region.ThinkingLine   term |> should equal "thinking..."

// ---------------------------------------------------------------------------
// Scenario: Placeholder replaced by input leaves no leftover characters
// ---------------------------------------------------------------------------
[<Fact>]
let ``placeholder replaced by input leaves no leftover characters`` () =
    let term = MockExecutor.create 40 10
    // Draw placeholder
    MockExecutor.execute (surface { yield paint (Region.InputArea 0) "Type a message or /help" Style.normal }) term |> ignore
    // Erase and draw real input
    let inputOps =
        surface {
            yield eraseLine (Region.InputArea 0)
            yield paint     (Region.InputArea 0) "> hello" Style.normal
        }
    MockExecutor.execute inputOps term |> ignore
    MockExecutor.regionContent (Region.InputArea 0) term |> should equal "> hello"

// ---------------------------------------------------------------------------
// Scenario: Modal takeover then ScrollContent resumes on scroll cursor row
// ---------------------------------------------------------------------------
[<Fact>]
let ``Modal paint then ScrollContent paint do not overwrite each other`` () =
    let term = MockExecutor.create 40 10
    term.CursorRow <- 3   // scroll cursor is at row 3
    // Modal paints row 0
    MockExecutor.execute [ DrawOp.Paint(Region.Modal, "[ picker ]", Style.normal) ] term |> ignore
    // ScrollContent writes at CursorRow (3)
    MockExecutor.execute [ DrawOp.Paint(Region.ScrollContent, "stream output", Style.normal) ] term |> ignore
    MockExecutor.lineAt 0 term |> should equal "[ picker ]"
    MockExecutor.lineAt 3 term |> should equal "stream output"

// ---------------------------------------------------------------------------
// Scenario: SaveAndRestore preserves cursor position
// ---------------------------------------------------------------------------
[<Fact>]
let ``SaveAndRestore restores cursor after inner ops`` () =
    let term = MockExecutor.create 40 10
    term.CursorRow <- 5
    term.CursorCol <- 10
    let inner = [ DrawOp.Paint(Region.StatusBarLine1, "tick", Style.normal) ]
    MockExecutor.execute [ DrawOp.SaveAndRestore inner ] term |> ignore
    // Cursor should be restored to 5/10
    term.CursorRow |> should equal 5
    term.CursorCol |> should equal 10
    // Inner op was applied — StatusBarLine1 (row 8) should have "tick"
    MockExecutor.lineAt 8 term |> should equal "tick"
