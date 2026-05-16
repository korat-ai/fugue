// =============================================================================
// PHASE 1 CONTRACT SKETCH — NOT THE FINAL `.fsi` IN src/.
//
// Feature:  002-spectre-full-coverage
// User Story: P5 (Console infrastructure & testing)
//
// Opaque `Console` wrapping either a production `IAnsiConsole` (via
// `Spectre.Console.AnsiConsole.Create(settings)` — NOT the obsolete
// `AnsiConsoleFactory`, per coverage-matrix discovery #2) or a
// `Spectre.Console.Testing.TestConsole` for deterministic capture.
//
// The `Console` is the parameter that `Live.run`, `Status.run`,
// `Progress.run`, and all four `Prompts.*.ask` functions take. This
// lets Phase 2 user-story implementations be exercised against
// `Console.test` in unit tests without spinning up a real terminal.
//
// Invariants:
//   * No Spectre type appears in this .fsi (SC-004). `TestConsole` is
//     wrapped, not re-exposed.
//   * `Console.default_` is a singleton; `Console.test` returns a
//     fresh instance per call.
//   * `Console.write` is total (Result-returning); `Console.capture`
//     is total on BOTH production and test consoles (revised from
//     research.md §R5 — see data-model.md §6.2 note).
// =============================================================================

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
    /// implementation uses `Spectre.Console.AnsiConsole.Create` (NOT
    /// the obsolete `AnsiConsoleFactory`).
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

    /// Read the current cursor state. For production, queries the
    /// terminal via Spectre's `IAnsiConsoleCursor`; for test, returns
    /// the test console's recorded state. Returns `UnknownState` on
    /// production consoles that do not support cursor-position
    /// queries (no Error path — cursor inquiry is best-effort).
    val cursorState : Console -> CursorState

module Exceptions =

    /// Render an exception via Spectre's built-in formatter (with
    /// default flags). Mirrors `AnsiConsoleExtensions.WriteException`
    /// but typed and `Result`-returning.
    val render : Console -> exn -> Result<unit, RenderError>

    /// Render an exception with custom format flags.
    val renderWith :
        Console -> ExceptionFormat -> exn -> Result<unit, RenderError>
