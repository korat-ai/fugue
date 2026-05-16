// =============================================================================
// PHASE 1 CONTRACT SKETCH — NOT THE FINAL `.fsi` IN src/.
//
// Feature:  002-spectre-full-coverage
// User Story: P3 (Live / streaming displays)
//
// Callback-scoped session APIs for Spectre's `Live`, `Status`, and
// `Progress` widgets. Per research.md §R2, the session handle is
// opaque, lives only inside the `body` callback, and is NOT returned
// from the `run` functions (preventing escape into outer scope).
//
// Cancellation is via native `CancellationToken`; on cancel the body
// is interrupted, Spectre's own try/finally tears down the cursor,
// and the wrapper surfaces `Error (UserCancelled "<op>")`.
//
// Concurrent live sessions on the same `Console` are rejected at the
// wrapper layer with `Error (ConcurrentLiveSession consoleId)` —
// Spectre's behaviour for nested live regions is undefined.
//
// Invariants:
//   * Sessions are not `IDisposable` at the F# surface (FR-010).
//   * `Spinner` is a closed DU mapping Spectre's `Spinner.Known.*`
//     registry; new Spectre spinners require a DU edit (caught by
//     CoverageMatrixTests reflection check, FR-019).
//   * No Spectre type appears in this .fsi (SC-004).
// =============================================================================

namespace Fugue.Adapters.Console

// -----------------------------------------------------------------------------
// Spinner — named spinner styles from Spectre's Known registry + Custom
// -----------------------------------------------------------------------------

/// Named spinner styles. One DU case per Spectre.Console 0.49.1
/// `Spinner.Known.*` static field. `Custom` allows user-defined
/// spinners without referencing Spectre types. Adding a Spectre
/// spinner requires extending this DU; the `CoverageMatrixTests`
/// reflection test (FR-019) will fail at the next Spectre bump if a
/// new spinner is missed.
type Spinner =
    | Default
    | Dots          | Dots2          | Dots3          | Dots4
    | Dots5         | Dots6          | Dots7          | Dots8
    | Dots9         | Dots10         | Dots11         | Dots12
    | Dots8Bit
    | Line          | Line2
    | Pipe          | SimpleDots     | SimpleDotsScrolling
    | Star          | Star2
    | Flip          | Hamburger      | GrowVertical   | GrowHorizontal
    | Balloon       | Balloon2       | Noise
    | Bounce        | BoxBounce      | BoxBounce2
    | Triangle      | Arc            | Circle
    | SquareCorners | CircleQuarters | CircleHalves
    | Squish
    | Toggle        | Toggle2        | Toggle3        | Toggle4
    | Toggle5       | Toggle6        | Toggle7        | Toggle8
    | Toggle9       | Toggle10       | Toggle11       | Toggle12
    | Toggle13
    | Arrow         | Arrow2         | Arrow3
    | BouncingBar   | BouncingBall
    | Smiley        | Monkey         | Hearts         | Clock
    | Earth         | Material       | Moon           | Runner
    | Pong          | Shark          | Dqpb           | Weather
    | Christmas     | Grenade        | Point          | Layer
    | BetaWave      | Aesthetic
    /// User-defined spinner. `frames` is the cycle frames (must be
    /// non-empty); `interval` is per-frame milliseconds (must be > 0).
    /// Validated lazily at point of use, not at DU construction.
    | Custom of frames: string list * interval: int

// -----------------------------------------------------------------------------
// Live — replaceable composition that refreshes on demand
// -----------------------------------------------------------------------------

[<Sealed>]
type LiveSession

module LiveSession =

    /// Replace the live composition with a new one. Spectre refreshes
    /// on its next tick. Returns Error if the underlying renderer
    /// fails or if the session has been torn down (i.e. update called
    /// after `Live.run`'s body returned).
    val update : LiveSession -> Composition -> Result<unit, RenderError>

    /// Force an immediate redraw (wraps Spectre's `ctx.Refresh()`).
    /// Used when the caller knows they have just updated the
    /// composition and want a visible response before the next tick.
    val refresh : LiveSession -> Result<unit, RenderError>

module Live =

    /// Run a live-updating composition for the duration of `body`.
    /// The `LiveSession` handle is valid only inside the callback;
    /// storing it elsewhere and calling methods after `run` returns
    /// produces `Error (RenderFailed ("LiveSession", "session used
    /// after run() returned"))`.
    ///
    /// Cancellation: when `cancellation` fires, `body` is interrupted
    /// via `OperationCanceledException`, Spectre's own try/finally
    /// tears down the cursor, and the wrapper returns
    /// `Error (UserCancelled "live")`.
    ///
    /// Concurrency: if `console` already has an active Live/Status/
    /// Progress session, returns `Error (ConcurrentLiveSession _)`
    /// immediately without invoking `body`.
    val run :
        console: Console ->
            initial: Composition ->
            cancellation: System.Threading.CancellationToken ->
            body: (LiveSession -> Async<Result<'T, RenderError>>) ->
            Async<Result<'T, RenderError>>

// -----------------------------------------------------------------------------
// Status — indeterminate spinner with a swappable message
// -----------------------------------------------------------------------------

[<Sealed>]
type StatusSession

module StatusSession =

    val updateMessage : StatusSession -> SafeText -> Result<unit, RenderError>

    val updateSpinner : StatusSession -> Spinner -> Result<unit, RenderError>

module Status =

    /// Same lifetime/cancellation/concurrency semantics as `Live.run`.
    /// On non-interactive consoles, degrades to a single final message
    /// line (no per-frame redraws) per FR-016.
    val run :
        console: Console ->
            spinner: Spinner ->
            message: SafeText ->
            cancellation: System.Threading.CancellationToken ->
            body: (StatusSession -> Async<Result<'T, RenderError>>) ->
            Async<Result<'T, RenderError>>

// -----------------------------------------------------------------------------
// Progress — multi-task progress display
// -----------------------------------------------------------------------------

[<Sealed>]
type ProgressSession

[<Sealed>]
type ProgressTask

module ProgressTask =

    /// Increment the task's value by `delta`. Bounded to [0, maxValue].
    val increment : ProgressTask -> delta: double -> Result<unit, RenderError>

    /// Set the task's value absolutely. Bounded to [0, maxValue].
    val setValue : ProgressTask -> value: double -> Result<unit, RenderError>

    /// Mark the task complete (sets value = maxValue).
    val complete : ProgressTask -> Result<unit, RenderError>

    /// Current progress value (0..maxValue).
    val value : ProgressTask -> double

    /// Task max value (default 100.0).
    val maxValue : ProgressTask -> double

module ProgressSession =

    /// Add a new task to the running progress session. Returns Error
    /// if the session has been torn down.
    val addTask :
        ProgressSession ->
            description: SafeText ->
            Result<ProgressTask, RenderError>

module Progress =

    /// Same lifetime/cancellation/concurrency semantics as `Live.run`.
    /// On non-interactive consoles, degrades to a single final tally
    /// line per FR-016.
    val run :
        console: Console ->
            cancellation: System.Threading.CancellationToken ->
            body: (ProgressSession -> Async<Result<'T, RenderError>>) ->
            Async<Result<'T, RenderError>>
