module Fugue.Tests.PickerPropertyTests

open FsCheck
open FsCheck.FSharp              // Gen.choose, Gen.constant, Gen.elements, Gen.listOfLength
open FsCheck.FSharp.GenBuilder   // brings `gen { }` CE into scope
open FsCheck.Xunit
open Fugue.Surface
open Fugue.Cli.Picker

// ---------------------------------------------------------------------------
// Custom Arbitrary for PickerState
// Generates valid combinations:
//   - Items: 0..20 items
//   - VisibleRows: 1..10
//   - ScrollTop: 0..(max 0 (n - visibleRows))
//   - CurrentIdx: 0..(max 0 (n-1))  (0 when empty)
// ---------------------------------------------------------------------------

type PickerArbs =
    static member PickerState() : Arbitrary<PickerState> =
        let genState = gen {
            let! n           = Gen.choose (0, 20)
            let! visibleRows = Gen.choose (1, 10)
            let  maxScroll   = max 0 (n - visibleRows)
            let! scrollTop   = Gen.choose (0, maxScroll)
            let! currentIdx  =
                if n = 0 then Gen.constant 0
                else Gen.choose (0, n - 1)
            // Use Gen.elements from a safe alphabet for labels / hints so Markup.Escape
            // is called on known-safe strings (avoids Spectre markup parse errors in tests).
            let safeStr = Gen.elements [ "alpha"; "beta"; "gamma"; "delta"; "epsilon"; "foo"; "bar"; "" ]
            let! title   = safeStr
            let! labels  = Gen.listOfLength n safeStr
            let! hints   = Gen.listOfLength n safeStr
            let items    = Array.zip (List.toArray labels) (List.toArray hints)
            return {
                Title       = title
                Items       = items
                CurrentIdx  = currentIdx
                ScrollTop   = scrollTop
                VisibleRows = visibleRows
            }
        }
        { new Arbitrary<PickerState>() with
            override _.Generator = genState
            override _.Shrinker _ = Seq.empty }

// ---------------------------------------------------------------------------
// Properties
// ---------------------------------------------------------------------------

[<Properties(Arbitrary = [| typeof<PickerArbs> |], MaxTest = 200, QuietOnSuccess = true)>]
module PickerProperties =

    // -----------------------------------------------------------------------
    // P1. Determinism: calling renderState twice with same state gives equal result
    // -----------------------------------------------------------------------
    [<Property>]
    let ``renderState is deterministic`` (state: PickerState) =
        renderState state = renderState state

    // -----------------------------------------------------------------------
    // P2. Op count: always exactly VisibleRows + 4
    //     (title + above-hint + visibleRows items + below-hint + footer)
    // -----------------------------------------------------------------------
    [<Property>]
    let ``renderState op count equals visibleRows + 4`` (state: PickerState) =
        let ops = renderState state
        let expected = state.VisibleRows + 4
        List.length ops = expected

    // -----------------------------------------------------------------------
    // P3. Region isolation: all ops are DrawOp.Append — no named-region paints
    // -----------------------------------------------------------------------
    [<Property>]
    let ``renderState uses only Append ops`` (state: PickerState) =
        renderState state
        |> List.forall (fun op ->
            match op with
            | DrawOp.Append _ -> true
            | _ -> false)

    // -----------------------------------------------------------------------
    // P4. Empty list: stable op count regardless of title or VisibleRows
    // -----------------------------------------------------------------------
    [<Property>]
    let ``empty items list always produces stable ops`` (visibleRows: PositiveInt) =
        let vr = min 10 visibleRows.Get
        let state =
            { Title       = "title"
              Items       = [||]
              CurrentIdx  = 0
              ScrollTop   = 0
              VisibleRows = vr }
        List.length (renderState state) = vr + 4

    // -----------------------------------------------------------------------
    // P5. Scroll consistency: above hint present iff ScrollTop > 0
    // -----------------------------------------------------------------------
    [<Property>]
    let ``above hint present iff ScrollTop > 0`` (state: PickerState) =
        let ops = renderState state
        // above-hint is ops.[1]
        let aboveText =
            match List.item 1 ops with
            | DrawOp.Append t -> t
            | _ -> ""
        let hasAbove = aboveText.Contains("more above")
        if state.ScrollTop > 0 then hasAbove
        else not hasAbove

    // -----------------------------------------------------------------------
    // P6. Below hint: present iff items extend beyond scroll window
    // -----------------------------------------------------------------------
    [<Property>]
    let ``below hint present iff items extend beyond window`` (state: PickerState) =
        let ops = renderState state
        // below-hint is ops.[2 + VisibleRows]
        let belowIdx  = 2 + state.VisibleRows
        let belowText =
            match List.item belowIdx ops with
            | DrawOp.Append t -> t
            | _ -> ""
        let remaining = state.Items.Length - (state.ScrollTop + state.VisibleRows)
        let hasBelow  = belowText.Contains("more below")
        if remaining > 0 then hasBelow
        else not hasBelow

    // -----------------------------------------------------------------------
    // P7. Highlight uniqueness: exactly one item row contains the › marker
    //     when CurrentIdx is inside the scroll window
    // -----------------------------------------------------------------------
    [<Property>]
    let ``selected marker appears exactly once when idx in view`` (state: PickerState) =
        if state.Items.Length = 0 then true
        else
            let inView =
                state.CurrentIdx >= state.ScrollTop &&
                state.CurrentIdx < state.ScrollTop + state.VisibleRows
            if not inView then true
            else
                let ops = renderState state
                // item rows are ops.[2 .. 2+VisibleRows-1]
                let itemRows =
                    ops
                    |> List.skip 2
                    |> List.take state.VisibleRows
                let markerCount =
                    itemRows
                    |> List.filter (fun op ->
                        match op with
                        | DrawOp.Append t -> t.Contains "›"
                        | _ -> false)
                    |> List.length
                markerCount = 1
