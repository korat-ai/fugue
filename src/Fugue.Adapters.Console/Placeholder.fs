namespace Fugue.Adapters.Console

/// Placeholder module — keeps the F# compiler happy while Phase 2 lands
/// the real foundation types (RenderError, Style, SafeText, RenderContext).
/// MUST be removed by Phase 2 T007 (which creates Errors.fs as the first
/// real file in compile order). Tracked: `tasks.md` Phase 2 checkpoint.
module internal Placeholder =
    let internal sentinel : unit = ()
