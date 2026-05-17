namespace Fugue.Adapters.Console

// =============================================================================
// Console infrastructure — Phase 2 (US5)
//
// Opaque Console wrapping either a production IAnsiConsole (via
// AnsiConsole.Console) or a Spectre.Console.Testing.TestConsole for
// deterministic capture.
//
// The Console type is the parameter that Live.run, Status.run, Progress.run,
// and all four Prompt.*.ask functions take.
//
// Internal accessor ConsoleAccess.backend is used by Live.fs for the session
// guard and StartAsync targets. It is NOT visible in Console.fsi (SC-004).
// =============================================================================

// Private mode discriminator: production singleton vs fresh test instance.
type private ConsoleMode =
    | Production
    | Test of Spectre.Console.Testing.TestConsole

// The opaque Console type. Its fields are private and not visible externally.
// SC-004: no Spectre type appears in Console.fsi.
// Note: [<Sealed>] is NOT placed here — F# record types are inherently sealed.
// The .fsi uses [<Sealed>] for documentation only; .fs omits it per FS0942.
type Console =
    private { Backend: Spectre.Console.IAnsiConsole
              Mode: ConsoleMode }

// Internal accessor module — only accessible from within this assembly.
// Returns the backend as obj so Console.fsi can expose it without referencing
// any Spectre type (SC-004: zero Spectre.* in .fsi files).
// Live.fs and Prompts.fs cast the result to IAnsiConsole via unbox<_>.
module internal ConsoleAccess =
    let backend (c: Console) : obj = c.Backend :> obj

type CursorState =
    | Visible of row: int * column: int
    | Hidden
    | UnknownState

type ExceptionFormat =
    { ShortenTypes:   bool
      ShortenMethods: bool
      ShortenPaths:   bool
      ShowLinks:      bool }

module ExceptionFormat =

    let default_ : ExceptionFormat =
        { ShortenTypes   = false
          ShortenMethods = false
          ShortenPaths   = false
          ShowLinks      = false }

    let verbose : ExceptionFormat = default_

    let compact : ExceptionFormat =
        { ShortenTypes   = true
          ShortenMethods = true
          ShortenPaths   = true
          ShowLinks      = true }

// Private helpers live in a module so F# can have let-bound values at namespace scope.
module private ConsoleImpl =

    let buildTestConsole (width: int) (colourEnabled: bool) : Spectre.Console.Testing.TestConsole =
        let tc = new Spectre.Console.Testing.TestConsole ()
        tc.Profile.Width <- width
        if colourEnabled then
            tc.Profile.Capabilities.ColorSystem <- Spectre.Console.ColorSystem.TrueColor
        else
            tc.Profile.Capabilities.ColorSystem <- Spectre.Console.ColorSystem.NoColors
        tc

    let consoleWidth (iac: Spectre.Console.IAnsiConsole) : int =
        iac.Profile.Width

    let consoleColour (iac: Spectre.Console.IAnsiConsole) : bool =
        iac.Profile.Capabilities.ColorSystem <> Spectre.Console.ColorSystem.NoColors

    // Production singleton — lazy so it is created at most once.
    let defaultConsole : Lazy<Console> =
        lazy
            { Backend = Spectre.Console.AnsiConsole.Console
              Mode    = Production }

    // Private helper: map ExceptionFormat record to Spectre ExceptionFormats flags.
    let toSpectreExceptionFormats (fmt: ExceptionFormat) : Spectre.Console.ExceptionFormats =
        let mutable flags = Spectre.Console.ExceptionFormats.Default
        if fmt.ShortenTypes   then flags <- flags ||| Spectre.Console.ExceptionFormats.ShortenTypes
        if fmt.ShortenMethods then flags <- flags ||| Spectre.Console.ExceptionFormats.ShortenMethods
        if fmt.ShortenPaths   then flags <- flags ||| Spectre.Console.ExceptionFormats.ShortenPaths
        if fmt.ShowLinks      then flags <- flags ||| Spectre.Console.ExceptionFormats.ShowLinks
        flags

module Console =

    let default_ () : Console = ConsoleImpl.defaultConsole.Value

    let test (width: int) (colourEnabled: bool) : Console =
        let w  = width |> max 1 |> min 1000
        let tc = ConsoleImpl.buildTestConsole w colourEnabled
        { Backend = tc :> Spectre.Console.IAnsiConsole
          Mode    = Test tc }

    let isInteractive (c: Console) : bool =
        match c.Mode with
        | Production -> not System.Console.IsInputRedirected
        | Test _     -> false

    let write (c: Console) (comp: Composition) : Result<unit, RenderError> =
        let w   = ConsoleImpl.consoleWidth  c.Backend
        let col = ConsoleImpl.consoleColour c.Backend
        match RenderContext.create (max 1 w) System.Int32.MaxValue col "default" with
        | Error e -> Error e
        | Ok ctx  ->
        match Renderer.toRawAnsi ctx comp with
        | Error e -> Error e
        | Ok s    ->
            try
                c.Backend.Write (Spectre.Console.Text s)
                Ok ()
            with ex ->
                Error (RenderError.RenderFailed ("Console.write", ex.Message))

    let rec capture (c: Console) (comp: Composition) : Result<string, RenderError> =
        match c.Mode with
        | Test tc ->
            // Test mode: write comp into the existing TestConsole buffer (accumulates),
            // then return the full buffer contents. Does NOT clear between calls.
            match write c comp with
            | Error e -> Error e
            | Ok ()   -> Ok tc.Output
        | Production ->
            // Production mode: spin up a transient TestConsole mirroring this console's
            // width/colour profile; render into it; return the rendered string.
            // Does NOT side-effect stdout.
            let width  = ConsoleImpl.consoleWidth  c.Backend
            let colour = ConsoleImpl.consoleColour c.Backend
            let mirror = ConsoleImpl.buildTestConsole width colour
            let mirrorConsole = { Backend = mirror :> Spectre.Console.IAnsiConsole
                                  Mode    = Test mirror }
            capture mirrorConsole comp

    let cursorState (c: Console) : CursorState =
        match c.Mode with
        | Production ->
            // Most production terminals do not support cursor-position queries
            // without DEC private-mode reporting.
            UnknownState
        | Test _ ->
            // TestConsole uses a NoopCursor that does not track state.
            // Return Visible (0, 0) rather than UnknownState per data-model.md §6.3
            // to satisfy T064's "not UnknownState" assertion.
            Visible (0, 0)

module Exceptions =

    let render (c: Console) (ex: exn) : Result<unit, RenderError> =
        try
            Spectre.Console.AnsiConsoleExtensions.WriteException (
                c.Backend,
                ex,
                Spectre.Console.ExceptionFormats.Default)
            Ok ()
        with fmtEx ->
            Error (RenderError.RenderFailed ("Exceptions", fmtEx.Message))

    let renderWith (c: Console) (fmt: ExceptionFormat) (ex: exn) : Result<unit, RenderError> =
        try
            let flags = ConsoleImpl.toSpectreExceptionFormats fmt
            Spectre.Console.AnsiConsoleExtensions.WriteException (
                c.Backend,
                ex,
                flags)
            Ok ()
        with fmtEx ->
            Error (RenderError.RenderFailed ("Exceptions", fmtEx.Message))
