module Fugue.Tests.StatusBarPropertyTests

open System
open FsCheck
open FsCheck.FSharp
open FsCheck.FSharp.GenBuilder
open FsCheck.Xunit
open Fugue.Surface
open Fugue.Core.Config
open Fugue.Core.Localization
open Fugue.Cli.StatusBar

// ---------------------------------------------------------------------------
// Custom Arbitrary for StatusBarState
// Generates valid, AOT-safe combinations without hitting real git or env vars.
// ---------------------------------------------------------------------------

type StatusBarArbs =
    static member StatusBarState() : Arbitrary<StatusBarState> =
        let safeStr = Gen.elements [ ""; "alpha"; "beta"; "foo/bar"; "my-branch"; "/projects/fugue" ]
        let genProvider = Gen.elements [
            Anthropic("key", "claude-opus-4-7")
            OpenAI("key", "gpt-4o")
            Ollama(Uri "http://localhost:11434", "llama3.1") ]
        let genCfg (prov: ProviderConfig) : AppConfig =
            { Provider        = prov
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
        let genOptCfg = gen {
            let! flag = Gen.elements [ true; false ]
            if flag then
                let! prov = genProvider
                return Some (genCfg prov)
            else
                return None
        }
        let genSnap = gen {
            let! sinceSeconds = Gen.choose (0, 120)
            let! frame        = Gen.choose (0, 100)
            let! tokenCount   = Gen.choose (0, 12)
            let  tokens       = Array.init tokenCount (fun i -> int64 i * 1_000_000L)
            return Some {
                Since          = DateTimeOffset.UtcNow.AddSeconds(float -sinceSeconds)
                SpinnerFrame   = frame
                TokensInWindow = tokens
            }
        }
        let genState = gen {
            let! cwd           = safeStr
            let! branch        = safeStr
            let! cfg           = genOptCfg
            let! locale        = Gen.elements [ "en"; "ru" ]
            let  strings       = pick locale
            let! theme         = Gen.elements [ ""; "nocturne" ]
            let! streaming     = Gen.frequency [(1, Gen.constant None); (1, genSnap)]
            let! lowBw         = Gen.elements [ true; false ]
            let! tmpl          = Gen.frequency [(2, Gen.constant None); (1, Gen.elements [Some "t1"; Some "t2"])]
            let! schedActive   = Gen.elements [ true; false ]
            let! annUp         = Gen.choose (0, 10)
            let! annDown       = Gen.choose (0, 10)
            let! sessionWords  = Gen.choose (0, 250_000)
            let! isSSH         = Gen.elements [ true; false ]
            let! width         = Gen.choose (60, 220)
            let! height        = Gen.choose (10, 60)
            return {
                Cwd           = cwd
                Branch        = branch
                Cfg           = cfg
                Strings       = strings
                ActiveTheme   = theme
                Streaming     = streaming
                LowBandwidth  = lowBw
                TemplateName  = tmpl
                ScheduleActive = schedActive
                Annotations   = { Up = annUp; Down = annDown }
                SessionWords  = sessionWords
                IsSSH         = isSSH
                Width         = width
                Height        = height
            }
        }
        { new Arbitrary<StatusBarState>() with
            override _.Generator  = genState
            override _.Shrinker _ = Seq.empty }

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

[<Properties(Arbitrary = [| typeof<StatusBarArbs> |], MaxTest = 300, QuietOnSuccess = true)>]
module StatusBarProperties =

    // -----------------------------------------------------------------------
    // P1. Determinism: same state → same DrawOp list (pure function)
    // Note: TimestampOffset.UtcNow is captured inside renderStatusBar for elapsed,
    // so two calls can differ by a few milliseconds.  The *structure* must match —
    // same op types, same regions, same op count — even if text differs by ≤1s.
    // We check structural equality (op types + regions), not full text equality.
    // -----------------------------------------------------------------------
    [<Property>]
    let ``renderStatusBar is structurally deterministic`` (state: StatusBarState) =
        let structureOf ops =
            ops |> List.map (fun op ->
                match op with
                | DrawOp.EraseLine r      -> ("erase", r, "")
                | DrawOp.Paint(r, _, _)   -> ("paint", r, "")
                | DrawOp.MoveTo(r, _)     -> ("moveto", r, "")
                | DrawOp.Append t         -> ("append", Region.ScrollContent, t)
                | DrawOp.SaveAndRestore _ -> ("sar", Region.ScrollContent, "")
                | DrawOp.ResetScrollRegion -> ("reset", Region.ScrollContent, ""))
        structureOf (renderStatusBar state) = structureOf (renderStatusBar state)

    // -----------------------------------------------------------------------
    // P2. Op count: always exactly 3
    // -----------------------------------------------------------------------
    [<Property>]
    let ``renderStatusBar always emits exactly 3 ops`` (state: StatusBarState) =
        List.length (renderStatusBar state) = 3

    // -----------------------------------------------------------------------
    // P3. Region locality: ops only target StatusBar regions — never InputArea,
    //     ScrollContent, or Modal
    // -----------------------------------------------------------------------
    [<Property>]
    let ``renderStatusBar ops only target StatusBar regions`` (state: StatusBarState) =
        let statusBarRegions = [ Region.ThinkingLine; Region.StatusBarLine1; Region.StatusBarLine2 ]
        renderStatusBar state
        |> List.forall (fun op ->
            match op with
            | DrawOp.Paint(r, _, _) -> List.contains r statusBarRegions
            | DrawOp.EraseLine r    -> List.contains r statusBarRegions
            | DrawOp.MoveTo(r, _)   -> List.contains r statusBarRegions
            | _                     -> true)  // SaveAndRestore/Append/Reset not expected but harmless

    // -----------------------------------------------------------------------
    // P4. Streaming invariant: Streaming=None → first op is EraseLine ThinkingLine.
    //     Streaming=Some → first op is Paint ThinkingLine.
    // -----------------------------------------------------------------------
    [<Property>]
    let ``streaming none means first op is EraseLine ThinkingLine`` (state: StatusBarState) =
        let s = { state with Streaming = None }
        match renderStatusBar s with
        | DrawOp.EraseLine Region.ThinkingLine :: _ -> true
        | _ -> false

    [<Property>]
    let ``streaming some means first op is Paint ThinkingLine`` (state: StatusBarState) =
        let snap = { Since = DateTimeOffset.UtcNow.AddSeconds(-1.0); SpinnerFrame = 0; TokensInWindow = [||] }
        let s = { state with Streaming = Some snap }
        match renderStatusBar s with
        | DrawOp.Paint(Region.ThinkingLine, _, _) :: _ -> true
        | _ -> false

    // -----------------------------------------------------------------------
    // P5. Locale isolation: changing Strings (locale) doesn't change op count
    //     or the set of regions targeted
    // -----------------------------------------------------------------------
    [<Property>]
    let ``locale change does not alter op count or regions`` (state: StatusBarState) =
        let regionSet ops =
            ops |> List.map (fun op ->
                match op with
                | DrawOp.Paint(r, _, _) -> r
                | DrawOp.EraseLine r    -> r
                | DrawOp.MoveTo(r, _)   -> r
                | _                     -> Region.ScrollContent)
            |> Set.ofList
        let stateEn = { state with Strings = en }
        let stateRu = { state with Strings = ru }
        List.length (renderStatusBar stateEn) = List.length (renderStatusBar stateRu)
        && regionSet (renderStatusBar stateEn) = regionSet (renderStatusBar stateRu)

    // -----------------------------------------------------------------------
    // P6. Width/Height invariance: changing dimensions doesn't alter op count
    // -----------------------------------------------------------------------
    [<Property>]
    let ``width and height changes do not alter op count`` (state: StatusBarState) =
        let s60  = { state with Width =  60; Height = 10 }
        let s220 = { state with Width = 220; Height = 60 }
        List.length (renderStatusBar s60) = List.length (renderStatusBar s220)

    // -----------------------------------------------------------------------
    // P7. Idempotence via MockExecutor:
    //     Applying renderStatusBar twice to the same fresh terminal produces
    //     the same Lines as applying it once.  (Named regions are overwritten,
    //     not accumulated.)
    // -----------------------------------------------------------------------
    [<Property>]
    let ``applying ops twice equals applying once (idempotence on named regions)`` (state: StatusBarState) =
        let ops  = renderStatusBar state
        // Two fresh terminals, identical geometry.
        let term1 = MockExecutor.create 120 30
        let term2 = MockExecutor.create 120 30
        MockExecutor.execute ops term1 |> ignore
        MockExecutor.execute ops term2 |> ignore
        MockExecutor.execute ops term2 |> ignore  // apply a second time to term2
        // All three status-bar rows should be identical because Paint overwrites.
        let h = 30
        let row r = MockExecutor.lineAt r
        // ThinkingLine = row h-3 = 27; StatusBarLine1 = h-2 = 28; StatusBarLine2 = h-1 = 29
        row (h - 3) term1 = row (h - 3) term2
        && row (h - 2) term1 = row (h - 2) term2
        && row (h - 1) term1 = row (h - 1) term2
