namespace Fugue.Adapters.Console

/// Immutable description of the host terminal. The adapter never reads
/// `System.Console` directly except inside `probe` — this isolates the
/// only IO probe to one function (data-model.md §6 invariant).
type RenderContext =
    private
        { width: int
          colourEnabled: bool
          themeName: string }
    member ColourEnabled: bool
    member ThemeName: string
    member Width: int

module RenderContext =

    /// Test-friendly constructor. No IO.
    val create:
        width: int -> colourEnabled: bool -> theme: (string | null) -> RenderContext

    /// Probe the host. The ONLY function in the adapter that reads
    /// `System.Console`. Never called from tests.
    val probe: unit -> RenderContext
