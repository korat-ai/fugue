namespace Fugue.Adapters.Console

/// Immutable description of the host terminal. The adapter never reads
/// `System.Console` directly except inside `probe` — this isolates the
/// only IO probe to one function (data-model.md §6 invariant).
type RenderContext =
    private
        { width: int
          height: int
          colourEnabled: bool
          themeName: string }
    member ColourEnabled: bool
    member ThemeName: string
    member Width: int
    /// Terminal height in rows. `System.Int32.MaxValue` signals "unconstrained"
    /// (the Phase 1/2 default — layout primitives treat it as "no vertical clip").
    member Height: int

module RenderContext =

    /// Test-friendly constructor. No IO.
    /// Returns Error if width ≤ 0 or height ≤ 0.
    val create:
        width: int -> height: int -> colourEnabled: bool -> theme: (string | null) ->
            Result<RenderContext, RenderError>

    /// Probe the host. The ONLY function in the adapter that reads
    /// `System.Console`. Never called from tests.
    val probe: unit -> RenderContext
