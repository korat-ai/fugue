namespace Fugue.Adapters.Console

/// Immutable description of the host terminal. The adapter never reads
/// `System.Console` directly except inside `probe` — this isolates the
/// only IO probe to one function (data-model.md §6 invariant).
type RenderContext =
    private { width: int; colourEnabled: bool; themeName: string }
    member this.Width         = this.width
    member this.ColourEnabled = this.colourEnabled
    member this.ThemeName     = this.themeName

module RenderContext =
    /// Test-friendly constructor. No IO.
    let create (width: int) (colourEnabled: bool) (theme: string | null) : RenderContext =
        let name =
            match theme with
            | null -> "default"
            | t    -> t
        { width = width; colourEnabled = colourEnabled; themeName = name }

    /// Probe the host. The ONLY function in the adapter that reads
    /// `System.Console`. Never called from tests.
    let probe () : RenderContext =
        let w =
            try System.Console.WindowWidth
            with _ -> 80
        // Colour heuristic: assume enabled if stdout is not redirected.
        // Swallowing the exception here is deliberate: the probe prefers safety
        // (assume no colour) over enablement. This matches the data-model.md §6
        // invariant that says "assume enabled if stdout is not redirected" — if
        // we can't even check, we fall back to disabled to avoid garbling output.
        let colour =
            try not System.Console.IsOutputRedirected
            with _ -> false
        { width = max 1 w; colourEnabled = colour; themeName = "default" }
