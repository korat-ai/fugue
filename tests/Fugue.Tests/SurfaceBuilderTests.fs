module Fugue.Tests.SurfaceBuilderTests

open Xunit
open FsUnit.Xunit
open Fugue.Surface

// ---------------------------------------------------------------------------
// 1. Empty builder yields []
// ---------------------------------------------------------------------------
[<Fact>]
let ``empty surface block yields empty list`` () =
    let ops = surface { () }
    ops |> List.isEmpty |> should equal true

// ---------------------------------------------------------------------------
// 2. Single paint yields one DrawOp.Paint
// ---------------------------------------------------------------------------
[<Fact>]
let ``single paint produces one DrawOp.Paint`` () =
    let ops = surface { yield paint Region.StatusBarLine1 "hello" Style.normal }
    ops |> should equal [ DrawOp.Paint(Region.StatusBarLine1, "hello", Style.normal) ]

// ---------------------------------------------------------------------------
// 3. Multiple ops compose left-to-right
// ---------------------------------------------------------------------------
[<Fact>]
let ``multiple ops compose in declaration order`` () =
    let ops =
        surface {
            yield paint    Region.StatusBarLine1 "line1" Style.normal
            yield eraseLine Region.StatusBarLine2
            yield append   "footer"
        }
    ops |> should equal
        [ DrawOp.Paint(Region.StatusBarLine1, "line1", Style.normal)
          DrawOp.EraseLine Region.StatusBarLine2
          DrawOp.Append "footer" ]

// ---------------------------------------------------------------------------
// 4a. if-true branch works with paint
// ---------------------------------------------------------------------------
[<Fact>]
let ``if-true branch emits Paint`` () =
    let ops =
        surface {
            if true then
                yield paint Region.ThinkingLine "thinking..." Style.dim
            else
                yield eraseLine Region.ThinkingLine
        }
    ops |> should equal [ DrawOp.Paint(Region.ThinkingLine, "thinking...", Style.dim) ]

// ---------------------------------------------------------------------------
// 4b. if-false branch works with eraseLine
// ---------------------------------------------------------------------------
[<Fact>]
let ``if-false branch emits EraseLine`` () =
    let ops =
        surface {
            if false then
                yield paint Region.ThinkingLine "thinking..." Style.dim
            else
                yield eraseLine Region.ThinkingLine
        }
    ops |> should equal [ DrawOp.EraseLine Region.ThinkingLine ]

// ---------------------------------------------------------------------------
// 5. for-loop yields ops per iteration
// ---------------------------------------------------------------------------
[<Fact>]
let ``for-loop yields one Append per iteration`` () =
    let items = [ "a"; "b"; "c" ]
    let ops =
        surface {
            for item in items do
                yield append item
        }
    ops |> should equal
        [ DrawOp.Append "a"
          DrawOp.Append "b"
          DrawOp.Append "c" ]

// ---------------------------------------------------------------------------
// 6. Default style (Style.normal) applies correctly
// ---------------------------------------------------------------------------
[<Fact>]
let ``paint with Style.normal is recorded correctly`` () =
    let ops = surface { yield paint Region.ScrollContent "text" Style.normal }
    ops |> should equal [ DrawOp.Paint(Region.ScrollContent, "text", Style.normal) ]

// ---------------------------------------------------------------------------
// 7. Explicit Style.bold is preserved
// ---------------------------------------------------------------------------
[<Fact>]
let ``paint with Style.bold preserves bold flag`` () =
    let ops = surface { yield paint Region.StatusBarLine1 "bold text" Style.bold }
    ops |> should equal
        [ DrawOp.Paint(Region.StatusBarLine1, "bold text", Style.bold) ]

// ---------------------------------------------------------------------------
// 8. moveTo appended correctly
// ---------------------------------------------------------------------------
[<Fact>]
let ``moveTo appends DrawOp.MoveTo with correct region and col`` () =
    let ops = surface { yield moveTo (Region.InputArea 0) 5 }
    ops |> should equal [ DrawOp.MoveTo(Region.InputArea 0, 5) ]

// ---------------------------------------------------------------------------
// 9. YieldFrom composes a sub-list into the parent
// ---------------------------------------------------------------------------
[<Fact>]
let ``yieldFrom embeds a DrawOp list into the parent`` () =
    let sub = [ DrawOp.Append "sub1"; DrawOp.Append "sub2" ]
    let ops =
        surface {
            yield paint Region.StatusBarLine1 "head" Style.normal
            yield! sub
        }
    ops |> should equal
        [ DrawOp.Paint(Region.StatusBarLine1, "head", Style.normal)
          DrawOp.Append "sub1"
          DrawOp.Append "sub2" ]

// ---------------------------------------------------------------------------
// 10. Conditional rendering: renderStatus spec example
// ---------------------------------------------------------------------------
let private renderStatus (cwd: string) (model: string) (streaming: bool) =
    surface {
        yield paint Region.StatusBarLine1 cwd   Style.dim
        yield paint Region.StatusBarLine2 model Style.dim
        if streaming then
            yield paint Region.ThinkingLine "thinking..." Style.dim
        else
            yield eraseLine Region.ThinkingLine
    }

[<Fact>]
let ``renderStatus streaming=true includes ThinkingLine paint`` () =
    let ops = renderStatus "/home" "claude" true
    ops |> List.length |> should equal 3
    ops.[2] |> should equal (DrawOp.Paint(Region.ThinkingLine, "thinking...", Style.dim))

[<Fact>]
let ``renderStatus streaming=false includes ThinkingLine erase`` () =
    let ops = renderStatus "/home" "claude" false
    ops |> List.length |> should equal 3
    ops.[2] |> should equal (DrawOp.EraseLine Region.ThinkingLine)
