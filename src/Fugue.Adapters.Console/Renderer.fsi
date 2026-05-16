namespace Fugue.Adapters.Console

/// Renderer evaluates Composition values to byte/escape streams.
/// Pure (modulo Spectre's internal allocations); never touches
/// `Console.Out` directly. Callers route the output to the Fugue.Surface
/// actor themselves (via `DrawOp.RawAnsi`). The integration seam
/// described in research.md §R4.
///
/// NOTE: `Renderer.render` was dropped from the public API. `Surface.batch`
/// lives in `Fugue.Cli.Surface` (REPL-only), not in `Fugue.Surface`;
/// pulling this adapter into Fugue.Cli's namespace violates the "pure render
/// layer" assumption (spec.md Assumptions). Callers post the DrawOp
/// themselves via their own Surface/actor wiring.
module Renderer =

    /// Evaluate a Composition into the byte/escape stream the Surface
    /// actor expects. Pure (no side effects on Console.Out, no actor post).
    val toRawAnsi:
        ctx: RenderContext -> composition: Composition -> Result<string, RenderError>

    /// Convenience: evaluate to a `DrawOp.RawAnsi` ready to hand to the actor.
    /// Caller posts the result via their existing `Surface` wrapper or
    /// directly via `MailboxProcessor.Post`.
    val toDrawOp:
        ctx: RenderContext ->
            composition: Composition -> Result<Fugue.Surface.DrawOp, RenderError>
