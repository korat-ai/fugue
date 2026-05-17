namespace Fugue.Adapters.Console

/// Opaque handle to an active console (production or test).
[<Sealed>]
type Console

/// Cursor-position state. Used by `Console.cursorState`.
type CursorState =
    /// Cursor is visible at a known position. (row, column) are
    /// 0-based.
    | Visible of row: int * column: int
    /// Cursor is hidden (Spectre's `cursor.Hide()` was called).
    | Hidden
    /// Cursor state is unknown — usually because the underlying
    /// console does not support cursor-position queries (most
    /// production terminals without DEC-private-mode reporting).
    | UnknownState

/// Format flags for exception rendering. Maps to Spectre's
/// `ExceptionFormats` `[<Flags>]` enum, but expressed as a record so
/// callers don't have to bit-or flags.
type ExceptionFormat =
    { ShortenTypes:   bool
      ShortenMethods: bool
      ShortenPaths:   bool
      ShowLinks:      bool }

module ExceptionFormat =

    /// All flags false. Identical to `verbose`.
    val default_ : ExceptionFormat

    /// All flags false. Full type / method / path display, no links.
    val verbose : ExceptionFormat

    /// Shorten everything; show clickable links.
    val compact : ExceptionFormat

module Console =

    /// Default production console: writes to stdout, respects terminal
    /// capabilities (auto-detected). Single shared instance across the
    /// process; subsequent calls return the same value. Underlying
    /// implementation uses `Spectre.Console.AnsiConsole.Console` directly.
    val default_ : unit -> Console

    /// Test console: captures all output to an in-memory buffer.
    /// Each call returns a fresh independent instance.
    /// `width` clamped to [1, 1000] on out-of-range input.
    val test : width: int -> colourEnabled: bool -> Console

    /// True iff this console can drive interactive prompts. For
    /// production: stdin is a TTY. For test: first-cut returns false
    /// (scripted-input support is a Phase 3 follow-up).
    val isInteractive : Console -> bool

    /// Render a Composition through this console. Production: writes
    /// to stdout. Test: appends to in-memory buffer.
    val write : Console -> Composition -> Result<unit, RenderError>

    /// Render a Composition into a string via this console's
    /// renderer. Works for both production and test consoles. For
    /// production, the result is the same ANSI string the console
    /// would have emitted (computed via a transient test console
    /// internally — does NOT write to stdout as a side effect).
    val capture : Console -> Composition -> Result<string, RenderError>

    /// Read the current cursor state. For production, returns
    /// `UnknownState` (most production terminals do not support
    /// cursor-position queries without DEC private-mode reporting).
    /// For test consoles, returns `Visible (0, 0)` (the NoopCursor
    /// does not track state but is always visible by default).
    val cursorState : Console -> CursorState

module Exceptions =

    /// Render an exception via Spectre's built-in formatter (with
    /// default flags). Mirrors `AnsiConsoleExtensions.WriteException`
    /// but typed and `Result`-returning.
    val render : Console -> exn -> Result<unit, RenderError>

    /// Render an exception with custom format flags.
    val renderWith :
        Console -> ExceptionFormat -> exn -> Result<unit, RenderError>

/// Internal accessor — visible only within this assembly.
/// Returns the Spectre IAnsiConsole backend as `obj` to keep Spectre types
/// out of the public .fsi surface (SC-004). Callers unbox to IAnsiConsole.
module internal ConsoleAccess =

    val backend : Console -> obj
