module Fugue.Adapters.Console.Tests.CompositionPropertyTests

// see Helpers.fs FACT_DISCOVERY for why all tests are [<Property>]
open FsCheck
open FsCheck.Xunit
open Fugue.Adapters.Console

// ============================================================================
// T039 (property variant) — algebraic invariants for composition smart ctors
// ============================================================================

/// T039 — stack of single element produces non-empty output if element does.
[<Property>]
let ``T039 — stack of single element is consistent with direct leaf render`` (s: string | null) =
    let ctx  = RenderContext.create 80 true "default"
    let safe = SafeText.ofUser s
    let leaf = Composition.Leaf (Primitive.Styled (Style.empty, safe))
    let stk  = Composition.stack [leaf]
    let resultLeaf = Renderer.toRawAnsi ctx leaf
    let resultStk  = Renderer.toRawAnsi ctx stk
    // Both must agree on Ok vs Error — stack of one must not introduce errors.
    match resultLeaf, resultStk with
    | Ok _,    Ok _    -> true
    | Error _, Error _ -> true
    | Ok _,    Error _ -> false   // stacking introduced a new error
    | Error _, Ok _    -> false   // unreachable (leaf errored, stack can't succeed)

/// padded with all-zero padding is at least as permissive as no padding.
[<Property>]
let ``T039b — padded 0 0 0 0 renders content without error`` (s: string | null) =
    let ctx  = RenderContext.create 80 true "default"
    let safe = SafeText.ofUser s
    let inner = Composition.Leaf (Primitive.Styled (Style.empty, safe))
    match Composition.padded 0 0 0 0 inner with
    | Error _ -> false   // zero padding must always succeed
    | Ok comp ->
        match Renderer.toRawAnsi ctx comp with
        | Ok _    -> true
        | Error _ -> false

/// columns with ratios summing to exactly 1.0 must be accepted.
[<Property(MaxTest = 1)>]
let ``T039c — columns with exactly 1.0 total ratio succeeds`` () =
    let a = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "L"))
    let b = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "R"))
    match Composition.columns [(0.5, a); (0.5, b)] with
    | Ok _    -> true
    | Error _ -> false

/// table with zero body rows is valid.
[<Property(MaxTest = 1)>]
let ``T039d — table with zero body rows and non-empty header is Ok`` () =
    let h = Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "Head"))
    match Composition.table [h] [] with
    | Ok _    -> true
    | Error _ -> false
