namespace Fugue.Adapters.Console

/// Immutable description of the host terminal. The adapter never reads
/// `System.Console` directly except inside `probe` — this isolates the
/// only IO probe to one function (data-model.md §6 invariant).
type RenderContext =
    private {
        Width:         int
        ColourEnabled: bool
        ThemeName:     string
    }
    member this.GetWidth         = this.Width
    member this.GetColourEnabled = this.ColourEnabled
    member this.GetThemeName     = this.ThemeName

module RenderContext =
    /// Test-friendly constructor. No IO.
    let create (width: int) (colourEnabled: bool) (theme: string | null) : RenderContext =
        let name =
            match theme with
            | null -> "default"
            | t    -> t
        { Width         = width
          ColourEnabled = colourEnabled
          ThemeName     = name }

    /// Probe the host. The ONLY function in the adapter that reads
    /// `System.Console`. Never called from tests.
    let probe () : RenderContext =
        let w =
            try System.Console.WindowWidth
            with _ -> 80
        // Colour heuristic: assume enabled if stdout is not redirected.
        let colour =
            try not System.Console.IsOutputRedirected
            with _ -> false
        { Width         = max 1 w
          ColourEnabled = colour
          ThemeName     = "default" }
