module Fugue.Adapters.Console.Tests.CorpusSnapshotTests

// see Helpers.fs FACT_DISCOVERY for why all tests are [<Property>]
open FsCheck.Xunit
open Fugue.Adapters.Console
open System.Text.RegularExpressions

// ============================================================================
// T048 — Combined corpus snapshot
//
// Verify.Xunit [<Fact>] tests are not discovered in this assembly (known
// issue — see Helpers.fs FACT_DISCOVERY). The workaround used in T048v:
//   [<Property(MaxTest=1)>] calls Verifier.Verify(...).ToTask().GetAwaiter().GetResult()
//
// ANSI CSI codes (\x1b[...m) are stripped before snapshotting so the baseline
// is readable and stable across Spectre version upgrades that might change
// colour-code formats. Content-level regressions (missing text, wrong layout)
// are still caught. Colour-level regressions are caught separately by T015
// which asserts \x1b[ appears when colour is enabled.
//
// First-run flow: the test writes CorpusSnapshotTests.T048v.received.txt
// next to the test assembly. Inspect it, then copy it to
// VisualParity/CorpusSnapshotTests.T048v.verified.txt in the repo.
// Subsequent runs compare against the committed .verified.txt.
// ============================================================================

/// Strip ANSI CSI escape sequences (\x1b[...m) leaving only printable content.
let private stripAnsi (s: string) : string =
    Regex.Replace(s, @"\x1b\[[0-9;]*[a-zA-Z]", "")

let private buildCorpus () : Composition =
    // Build a representative multi-primitive corpus exercising all major
    // composition types introduced in Phase 4 (US2).
    let styledLeaf text =
        Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral text))

    let errorStyle =
        Style.create (Colour.Theme "error") Colour.Default [Decoration.Bold]
        |> function Ok s -> s | Error _ -> Style.empty

    let dimStyle =
        Style.create (Colour.Theme "dim-accent") Colour.Default []
        |> function Ok s -> s | Error _ -> Style.empty

    // Stack of styled items (the most common composition pattern).
    let styleOk r = match r with Ok s -> s | Error _ -> Style.empty
    let addStyle    = Style.create (Colour.Theme "diff-add")    Colour.Default [] |> styleOk
    let removeStyle = Style.create (Colour.Theme "diff-remove") Colour.Default [] |> styleOk
    let hunkStyle   = Style.create (Colour.Theme "diff-hunk")   Colour.Default [] |> styleOk
    let diffLines =
        Composition.stack [
            Composition.Leaf (Primitive.Styled (addStyle,    SafeText.ofLiteral "+ added line"))
            Composition.Leaf (Primitive.Styled (removeStyle, SafeText.ofLiteral "- removed line"))
            Composition.Leaf (Primitive.Styled (hunkStyle,   SafeText.ofLiteral "@@ hunk header @@"))
        ]

    // Padded content.
    let paddedInfo =
        Composition.padded 2 2 0 0 (styledLeaf "padded info text")
        |> function Ok c -> c | Error _ -> styledLeaf "(padding error)"

    // Panel wrapping the diff lines.
    let panel =
        Composition.panel Border.Rounded (Some (SafeText.ofLiteral " Diff ")) diffLines

    // Error-styled aligned text.
    let errorLine =
        Composition.aligned Alignment.RightAlign
            (Composition.Leaf (Primitive.Styled (errorStyle, SafeText.ofLiteral "error message")))

    // Table with 2 columns × 2 rows.
    let tableResult =
        Composition.table
            [styledLeaf "Key"; styledLeaf "Value"]
            [[styledLeaf "alpha"; styledLeaf "1"]
             [styledLeaf "beta";  styledLeaf "2"]]

    let table =
        match tableResult with
        | Ok t  -> t
        | Error _ -> styledLeaf "(table error)"

    // Dim-accent-styled separator.
    let dimSep =
        Composition.Leaf (Primitive.Styled (dimStyle, SafeText.ofLiteral "────────────"))

    // Assemble everything into a top-level stack.
    Composition.stack [
        panel
        dimSep
        paddedInfo
        errorLine
        dimSep
        table
        Composition.Leaf Primitive.LineBreak
    ]

[<Property(MaxTest = 1)>]
let ``T048 — Combined corpus renders without error at width 80 colour enabled`` () =
    let ctx    =
        RenderContext.create 80 System.Int32.MaxValue true "default"
        |> function Ok c -> c | Error e -> failwith $"test ctx: {e}"
    let corpus = buildCorpus ()
    match Renderer.toRawAnsi ctx corpus with
    | Error _ -> false
    | Ok s    ->
        // Must contain representative content from each section.
        s.Contains "added line"
        && s.Contains "removed line"
        && s.Contains "padded info text"
        && s.Contains "error message"
        && s.Contains "alpha"

[<Property(MaxTest = 1)>]
let ``T048b — Combined corpus renders without error at width 80 colour disabled`` () =
    let ctx    =
        RenderContext.create 80 System.Int32.MaxValue false "default"
        |> function Ok c -> c | Error e -> failwith $"test ctx: {e}"
    let corpus = buildCorpus ()
    match Renderer.toRawAnsi ctx corpus with
    | Error _ -> false
    | Ok s    ->
        // Content must be present (colour may or may not emit resets —
        // the NoColors mode suppresses colour codes but structural ANSI
        // codes like cursor moves can still appear from Spectre layout).
        s.Contains "added line"
        && s.Contains "alpha"

[<Property(MaxTest = 1)>]
let ``T048c — Combined corpus output is deterministic (two renders agree)`` () =
    let ctx    =
        RenderContext.create 80 System.Int32.MaxValue true "nocturne"
        |> function Ok c -> c | Error e -> failwith $"test ctx: {e}"
    let corpus = buildCorpus ()
    match Renderer.toRawAnsi ctx corpus, Renderer.toRawAnsi ctx corpus with
    | Ok s1, Ok s2 -> s1 = s2
    | Error _, Error _ -> true
    | _ -> false

// ============================================================================
// T048v — Snapshot baseline for combined corpus (review #14)
//
// Verify.Xunit.Verifier.Verify() requires xunit's test execution context,
// which is NOT available when called from inside [<Property>] (FsCheck runs
// in its own scope). The workaround: manage the snapshot file ourselves with
// plain file I/O, following the same Verify conventions:
//   - VisualParity/T048v_corpus_snapshot.verified.txt  — committed baseline
//   - VisualParity/T048v_corpus_snapshot.received.txt  — written on mismatch
//
// On first run: the test writes T048v_corpus_snapshot.received.txt and fails.
// The engineer inspects it, renames it to .verified.txt, and commits it.
// On subsequent runs: the test compares against .verified.txt byte-for-byte.
//
// ANSI CSI escape codes are stripped before comparison so the baseline is
// readable and stable across Spectre version upgrades.
// ============================================================================

[<Property(MaxTest = 1)>]
let ``T048v — Snapshot baseline of combined corpus (ANSI-scrubbed)`` () =
    let ctx    =
        RenderContext.create 80 System.Int32.MaxValue false "default"
        |> function Ok c -> c | Error e -> failwith $"test ctx: {e}"
    let corpus = buildCorpus ()
    match Renderer.toRawAnsi ctx corpus with
    | Error _ -> false
    | Ok raw  ->
        let scrubbed = stripAnsi raw
        // Pre-checks: text content must survive stripping.
        let contentOk =
            scrubbed.Contains "added line"
            && scrubbed.Contains "padded info text"
            && scrubbed.Contains "error message"
            && scrubbed.Contains "alpha"
        if not contentOk then false
        else
            let dir      = System.IO.Path.Combine(__SOURCE_DIRECTORY__, "VisualParity")
            let verified = System.IO.Path.Combine(dir, "T048v_corpus_snapshot.verified.txt")
            let received = System.IO.Path.Combine(dir, "T048v_corpus_snapshot.received.txt")
            if not (System.IO.File.Exists verified) then
                // First run: write received and fail so the engineer can promote it.
                System.IO.File.WriteAllText(received, scrubbed)
                eprintfn "T048v: no .verified.txt found. Wrote:\n  %s\nInspect and rename to .verified.txt to commit the baseline." received
                false
            else
                let baseline = System.IO.File.ReadAllText verified
                if scrubbed = baseline then
                    // Clean up any stale received file from a previous mismatch run.
                    if System.IO.File.Exists received then System.IO.File.Delete received
                    true
                else
                    System.IO.File.WriteAllText(received, scrubbed)
                    eprintfn "T048v: snapshot mismatch. Diff:\n  baseline: %s\n  received: %s" verified received
                    false
