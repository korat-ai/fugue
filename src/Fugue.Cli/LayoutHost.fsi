module Fugue.Cli.LayoutHost

// =============================================================================
// LayoutHost — wires LayoutScheduler to the Surface actor (data-model §0.2).
//
// This is the JIT-only integration shim between the Phase 3 layout framework
// (Fugue.Adapters.Console.Layout.LayoutScheduler) and the Surface actor
// (Fugue.Surface.RealExecutor via DrawOp.RawAnsi).
//
// Callers:
//   1. Repl.fs — mounts one scheduler per REPL session, triggers on each
//      incoming LLM token.
//
// Design (R-2 from research.md §R-2, §0.2):
//   - Layout above Surface: LayoutScheduler produces Composition, LayoutHost
//     converts to ANSI via Renderer.toRawAnsi, posts DrawOp.RawAnsi to
//     the actor. Surface is NOT modified.
//   - Status bar and input region continue to use DrawOp directly.
//   - SIGWINCH: RenderContext.probe() is called fresh inside each onFrame
//     callback so every tick picks up the current terminal dimensions.
// =============================================================================

open Fugue.Adapters.Console
open Fugue.Adapters.Console.Layout

/// Mount a layout-producer function into the Surface actor for the scroll-content
/// region. Returns a `LayoutScheduler` handle.
///
/// - `producer`: function called at most once per `frameIntervalMs` that builds
///   the current layout tree. May read caller state via closure.
/// - `frameIntervalMs`: tick interval in ms. Default: 16 (≈ 60 Hz).
///
/// The scheduler is started immediately. Call `Scheduler.stop` on the
/// returned handle when the session ends (e.g. REPL shutdown).
val mount :
    producer: (unit -> Result<Composition, RenderError>) ->
        frameIntervalMs: int ->
        LayoutScheduler
