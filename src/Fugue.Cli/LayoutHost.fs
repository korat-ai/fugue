module Fugue.Cli.LayoutHost

open Fugue.Adapters.Console
open Fugue.Adapters.Console.Layout

// =============================================================================
// LayoutHost — integration shim (data-model.md §0.2, research.md R-2).
//
// Bridges the Phase 3 layout scheduler to the Fugue.Surface DrawOp actor.
// ~50 LOC per research.md R-2 estimate.
// =============================================================================

/// Mount a layout-producer into the Surface actor for the scroll-content region.
///
/// On each scheduler tick, if the producer succeeds and the resulting Composition
/// is new (referential-equality short-circuit), the LayoutScheduler calls onFrame.
/// onFrame here: probes the live terminal dimensions, runs Renderer.toRawAnsi,
/// and posts DrawOp.RawAnsi to the Surface actor.
///
/// SIGWINCH is handled passively: RenderContext.probe() inside onFrame reads
/// System.Console.WindowWidth / WindowHeight on every frame, so the next tick
/// after a resize automatically uses the new dimensions. No explicit resize
/// notification is needed at the layout level (research.md §B-2).
let mount
        (producer: unit -> Result<Composition, RenderError>)
        (frameIntervalMs: int) : LayoutScheduler =

    let onFrame (comp: Composition) =
        // Probe the live terminal dimensions on every frame.
        // This is the SIGWINCH detection mechanism: Console.WindowWidth and
        // Console.WindowHeight are re-read each tick (research.md §B-2).
        let ctx = RenderContext.probe ()
        match Renderer.toRawAnsi ctx comp with
        | Ok ansi ->
            // Post to the Surface actor via DrawOp.RawAnsi.
            Surface.write ansi
        | Error e ->
            // Log render errors to stderr; don't crash the scheduler.
            System.Console.Error.Write $"[layout render error: {e}]"

    Scheduler.create producer frameIntervalMs onFrame
