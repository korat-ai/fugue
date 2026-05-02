module Fugue.Tests.StatusBarRenderTests

open System
open Xunit
open FsUnit.Xunit
open Fugue.Surface
open Fugue.Core
open Fugue.Core.Config
open Fugue.Core.Localization
open Fugue.Cli.StatusBar

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

let private defaultProvider = Anthropic("key", "claude-opus-4-7")

let private defaultCfg : AppConfig =
    { Provider        = defaultProvider
      SystemPrompt    = None
      ProfileContent  = None
      TemplateContent = None
      TemplateName    = None
      MaxIterations   = 5
      MaxTokens       = None
      Ui              = defaultUi ()
      BaseUrl         = None
      LowBandwidth    = false
      Offline         = false
      DryRun          = false }

/// Minimal idle state — no streaming, no badges, en locale.
let private idleState : StatusBarState =
    { Cwd           = "/home/user/project"
      Branch        = "main"
      Cfg           = Some defaultCfg
      Strings       = en
      ActiveTheme   = ""
      Streaming     = None
      LowBandwidth  = false
      TemplateName  = None
      ScheduleActive = false
      Annotations   = { Up = 0; Down = 0 }
      SessionWords  = 0
      IsSSH         = false
      Width         = 120
      Height        = 30
      ApprovalMode  = ApprovalMode.Default }

/// Apply ops to a fresh 120×30 mock terminal and return it.
let private exec (ops: DrawOp list) : MockTerminal =
    let term = MockExecutor.create 120 30
    MockExecutor.execute ops term |> ignore
    term

// ---------------------------------------------------------------------------
// 1. Idle state — ThinkingLine has EraseLine, not Paint
// ---------------------------------------------------------------------------

[<Fact>]
let ``idle state: ThinkingLine op is EraseLine`` () =
    let ops = renderStatusBar idleState
    // First op must be EraseLine Region.ThinkingLine
    match ops with
    | DrawOp.EraseLine Region.ThinkingLine :: _ -> ()
    | _ -> failwith $"Expected EraseLine ThinkingLine first; got: {ops}"

[<Fact>]
let ``idle state: exactly 3 ops emitted`` () =
    renderStatusBar idleState |> List.length |> should equal 3

[<Fact>]
let ``idle state: no spinner-frame character in any op text`` () =
    let spinnerChars = [| "♩"; "♪"; "♫"; "♬"; "♭"; "♮"; "♯" |]
    let ops = renderStatusBar idleState
    let allText =
        ops |> List.choose (fun op ->
            match op with
            | DrawOp.Paint(_, t, _) -> Some t
            | _ -> None)
        |> String.concat ""
    let hasSpinner = spinnerChars |> Array.exists allText.Contains
    hasSpinner |> should equal false

// ---------------------------------------------------------------------------
// 2. Streaming state — ThinkingLine has Paint with spinner char + elapsed
// ---------------------------------------------------------------------------

[<Fact>]
let ``streaming state: ThinkingLine op is Paint`` () =
    let snap = { Since = DateTimeOffset.UtcNow.AddSeconds(-2.0); SpinnerFrame = 0; TokensInWindow = [||] }
    let s = { idleState with Streaming = Some snap }
    let ops = renderStatusBar s
    match ops with
    | DrawOp.Paint(Region.ThinkingLine, _, _) :: _ -> ()
    | _ -> failwith $"Expected Paint ThinkingLine first; got: {ops}"

[<Fact>]
let ``streaming state: ThinkingLine text contains a spinner-frame character`` () =
    let spinnerChars = [| "♩"; "♪"; "♫"; "♬"; "♭"; "♮"; "♯" |]
    let snap = { Since = DateTimeOffset.UtcNow.AddSeconds(-1.0); SpinnerFrame = 0; TokensInWindow = [||] }
    let s = { idleState with Streaming = Some snap }
    let ops = renderStatusBar s
    let thinkingText =
        ops |> List.pick (fun op ->
            match op with
            | DrawOp.Paint(Region.ThinkingLine, t, _) -> Some t
            | _ -> None)
    let hasSpinner = spinnerChars |> Array.exists thinkingText.Contains
    hasSpinner |> should equal true

[<Fact>]
let ``streaming state: ThinkingLine text contains thinking string`` () =
    let snap = { Since = DateTimeOffset.UtcNow.AddSeconds(-1.0); SpinnerFrame = 0; TokensInWindow = [||] }
    let s = { idleState with Streaming = Some snap }
    let ops = renderStatusBar s
    let thinkingText =
        ops |> List.pick (fun op ->
            match op with
            | DrawOp.Paint(Region.ThinkingLine, t, _) -> Some t
            | _ -> None)
    thinkingText |> should haveSubstring en.Thinking

// ---------------------------------------------------------------------------
// 3. Line1 contains cwd and branch
// ---------------------------------------------------------------------------

[<Fact>]
let ``line1 contains cwd path`` () =
    let s = { idleState with Cwd = "/projects/fugue"; Branch = "feat/foo" }
    let ops = renderStatusBar s
    let line1 =
        ops |> List.pick (fun op ->
            match op with
            | DrawOp.Paint(Region.StatusBarLine1, t, _) -> Some t
            | _ -> None)
    line1 |> should haveSubstring "/projects/fugue"

[<Fact>]
let ``line1 contains branch name`` () =
    let s = { idleState with Cwd = "/projects/fugue"; Branch = "feat/my-branch" }
    let ops = renderStatusBar s
    let line1 =
        ops |> List.pick (fun op ->
            match op with
            | DrawOp.Paint(Region.StatusBarLine1, t, _) -> Some t
            | _ -> None)
    line1 |> should haveSubstring "feat/my-branch"

// ---------------------------------------------------------------------------
// 4. SSH badge appears in line2 when IsSSH = true
// ---------------------------------------------------------------------------

[<Fact>]
let ``SSH badge appears in line2 when IsSSH is true`` () =
    let s = { idleState with IsSSH = true }
    let ops = renderStatusBar s
    let line2 =
        ops |> List.pick (fun op ->
            match op with
            | DrawOp.Paint(Region.StatusBarLine2, t, _) -> Some t
            | _ -> None)
    line2 |> should haveSubstring "SSH"

[<Fact>]
let ``SSH badge absent from line2 when IsSSH is false`` () =
    let s = { idleState with IsSSH = false }
    let ops = renderStatusBar s
    let line2 =
        ops |> List.pick (fun op ->
            match op with
            | DrawOp.Paint(Region.StatusBarLine2, t, _) -> Some t
            | _ -> None)
    line2 |> should not' (haveSubstring "SSH")

// ---------------------------------------------------------------------------
// 5. Context % badge color thresholds
// ---------------------------------------------------------------------------

[<Fact>]
let ``ctx badge is green when usage is below 50 pct`` () =
    // claude window = 200_000; ~50% = 150_000 tokens ~ 112_500 words
    // Use 10_000 words → ~7.5% → green
    let s = { idleState with SessionWords = 10_000 }
    let ops = renderStatusBar s
    let line2 =
        ops |> List.pick (fun op ->
            match op with
            | DrawOp.Paint(Region.StatusBarLine2, t, _) -> Some t
            | _ -> None)
    // green = \x1b[32m
    line2 |> should haveSubstring "\x1b[32m"

[<Fact>]
let ``ctx badge is yellow when usage is 50-79 pct`` () =
    // claude window = 200_000; 50% tokens = 100_000 → 75_000 words
    let s = { idleState with SessionWords = 75_000 }
    let ops = renderStatusBar s
    let line2 =
        ops |> List.pick (fun op ->
            match op with
            | DrawOp.Paint(Region.StatusBarLine2, t, _) -> Some t
            | _ -> None)
    // yellow = \x1b[33m
    line2 |> should haveSubstring "\x1b[33m"

[<Fact>]
let ``ctx badge is red when usage is 80 pct or more`` () =
    // claude window = 200_000; 80% tokens = 160_000 → 120_000 words
    let s = { idleState with SessionWords = 120_000 }
    let ops = renderStatusBar s
    let line2 =
        ops |> List.pick (fun op ->
            match op with
            | DrawOp.Paint(Region.StatusBarLine2, t, _) -> Some t
            | _ -> None)
    // red = \x1b[31m  (not from annotation badge in this state — annots=0)
    line2 |> should haveSubstring "\x1b[31m"

// ---------------------------------------------------------------------------
// 6. Low-bandwidth badge
// ---------------------------------------------------------------------------

[<Fact>]
let ``low-bw badge appears in line2 when LowBandwidth is true`` () =
    let s = { idleState with LowBandwidth = true }
    let ops = renderStatusBar s
    let line2 =
        ops |> List.pick (fun op ->
            match op with
            | DrawOp.Paint(Region.StatusBarLine2, t, _) -> Some t
            | _ -> None)
    line2 |> should haveSubstring "low-bw"

// ---------------------------------------------------------------------------
// 7. Template badge
// ---------------------------------------------------------------------------

[<Fact>]
let ``template badge appears in line2 when TemplateName is Some`` () =
    let s = { idleState with TemplateName = Some "mytemplate" }
    let ops = renderStatusBar s
    let line2 =
        ops |> List.pick (fun op ->
            match op with
            | DrawOp.Paint(Region.StatusBarLine2, t, _) -> Some t
            | _ -> None)
    line2 |> should haveSubstring "tmpl:mytemplate"

// ---------------------------------------------------------------------------
// 8. Schedule badge
// ---------------------------------------------------------------------------

[<Fact>]
let ``schedule badge ⏱ appears in line2 when ScheduleActive is true`` () =
    let s = { idleState with ScheduleActive = true }
    let ops = renderStatusBar s
    let line2 =
        ops |> List.pick (fun op ->
            match op with
            | DrawOp.Paint(Region.StatusBarLine2, t, _) -> Some t
            | _ -> None)
    line2 |> should haveSubstring "⏱"

// ---------------------------------------------------------------------------
// 9. Zero annotations → no ↑/↓ in line2
// ---------------------------------------------------------------------------

[<Fact>]
let ``zero annotations: no up-down arrow in line2`` () =
    let s = { idleState with Annotations = { Up = 0; Down = 0 } }
    let ops = renderStatusBar s
    let line2 =
        ops |> List.pick (fun op ->
            match op with
            | DrawOp.Paint(Region.StatusBarLine2, t, _) -> Some t
            | _ -> None)
    line2 |> should not' (haveSubstring "↑")
    line2 |> should not' (haveSubstring "↓")

// ---------------------------------------------------------------------------
// 10. Non-zero annotations → ↑/↓ appear in line2
// ---------------------------------------------------------------------------

[<Fact>]
let ``non-zero annotations: arrows appear in line2`` () =
    let s = { idleState with Annotations = { Up = 3; Down = 1 } }
    let ops = renderStatusBar s
    let line2 =
        ops |> List.pick (fun op ->
            match op with
            | DrawOp.Paint(Region.StatusBarLine2, t, _) -> Some t
            | _ -> None)
    line2 |> should haveSubstring "↑3"
    line2 |> should haveSubstring "↓1"

// ---------------------------------------------------------------------------
// 11. Locale changes reflect in StatusBarApp / Thinking strings
// ---------------------------------------------------------------------------

[<Fact>]
let ``Russian locale: Thinking string uses Russian text`` () =
    let snap = { Since = DateTimeOffset.UtcNow.AddSeconds(-1.0); SpinnerFrame = 0; TokensInWindow = [||] }
    let s = { idleState with Strings = ru; Streaming = Some snap }
    let ops = renderStatusBar s
    let thinkingText =
        ops |> List.pick (fun op ->
            match op with
            | DrawOp.Paint(Region.ThinkingLine, t, _) -> Some t
            | _ -> None)
    thinkingText |> should haveSubstring ru.Thinking

[<Fact>]
let ``Russian locale: line2 contains ru StatusBarApp`` () =
    let s = { idleState with Strings = ru }
    let ops = renderStatusBar s
    let line2 =
        ops |> List.pick (fun op ->
            match op with
            | DrawOp.Paint(Region.StatusBarLine2, t, _) -> Some t
            | _ -> None)
    // Both en and ru have "fugue" as StatusBarApp — check the on/na branch string in line1 instead
    let line1 =
        ops |> List.pick (fun op ->
            match op with
            | DrawOp.Paint(Region.StatusBarLine1, t, _) -> Some t
            | _ -> None)
    line1 |> should haveSubstring ru.StatusBarOn

// ---------------------------------------------------------------------------
// 12. No Cfg → line2 still renders (no crash, uses StatusBarApp)
// ---------------------------------------------------------------------------

[<Fact>]
let ``no cfg: line2 renders without crashing and contains StatusBarApp`` () =
    let s = { idleState with Cfg = None }
    let ops = renderStatusBar s
    ops |> List.length |> should equal 3
    let line2 =
        ops |> List.pick (fun op ->
            match op with
            | DrawOp.Paint(Region.StatusBarLine2, t, _) -> Some t
            | _ -> None)
    line2 |> should haveSubstring en.StatusBarApp

// ---------------------------------------------------------------------------
// PR B integration: verify captureState→renderStatusBar pipeline convergence
// These tests exercise renderStatusBar with state that mirrors what captureState
// would produce from known inputs — ensuring no divergence between the pure
// render and the old inline path's output contract.
// ---------------------------------------------------------------------------

[<Fact>]
let ``renderStatusBar line2 contains provider label when cfg is present`` () =
    let s = { idleState with Cfg = Some defaultCfg }
    let ops = renderStatusBar s
    let line2 =
        ops |> List.pick (fun op ->
            match op with
            | DrawOp.Paint(Region.StatusBarLine2, t, _) -> Some t
            | _ -> None)
    // Provider label format: "anthropic:claude-opus-4-7" or display name
    line2 |> should haveSubstring "anthropic:"

[<Fact>]
let ``renderStatusBar re-renders consistently after state change (cfg swap)`` () =
    // Simulate what refresh() does after setCfg: build state with new cfg, re-render.
    let cfg1 = defaultCfg
    let cfg2 = { defaultCfg with Provider = OpenAI("key2", "gpt-4o") }
    let s1 = { idleState with Cfg = Some cfg1 }
    let s2 = { idleState with Cfg = Some cfg2 }
    let ops1 = renderStatusBar s1
    let ops2 = renderStatusBar s2
    let line2Of ops =
        ops |> List.pick (fun op ->
            match op with
            | DrawOp.Paint(Region.StatusBarLine2, t, _) -> Some t
            | _ -> None)
    let text1 = line2Of ops1
    let text2 = line2Of ops2
    text1 |> should haveSubstring "anthropic:"
    text2 |> should haveSubstring "openai:"

[<Fact>]
let ``renderStatusBar state snapshot preserves cwd home-relative form`` () =
    // Verify that cwd passed in as-is ends up in line1 — no double-transformation.
    // (captureState calls homeRel before building state; renderStatusBar uses Cwd verbatim.)
    let s = { idleState with Cwd = "~/projects/myapp"; Branch = "develop" }
    let ops = renderStatusBar s
    let line1 =
        ops |> List.pick (fun op ->
            match op with
            | DrawOp.Paint(Region.StatusBarLine1, t, _) -> Some t
            | _ -> None)
    line1 |> should haveSubstring "~/projects/myapp"
