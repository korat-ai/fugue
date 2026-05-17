// =============================================================================
// Prompts — Phase 2 (US4)
//
// Async F# wrappers over the upstream library's four interactive prompt types.
// Per data-model.md §5 and research.md §R3:
//   * Return shape:     Async<Result<'T, RenderError>>
//   * Cancellation:     native CancellationToken passed to the underlying
//                       ShowAsync, with a pre-flight check before entry.
//   * Choice labels:    Composition (lowered to plain-text via the
//                       projection from data-model.md §0.1; colour stripped).
//   * Validator:        typed F# `string -> Result<'T, string>`.
//
// Invariants:
//   * No upstream library type appears in this .fsi (SC-004).
//   * Empty choice lists rejected before ShowAsync (FR-005).
//   * Non-TTY consoles short-circuit with Error NoInteractiveConsole (FR-016).
//   * Cancellation always surfaces as Error (UserCancelled "<op>") (FR-013).
// =============================================================================

namespace Fugue.Adapters.Console

/// Typed validator passed to `TextPrompt.ask`. Returns `Ok` with the
/// parsed value on success, or `Error` with a user-visible message
/// to display in the underlying library's native re-prompt loop.
/// The wrapper invokes the validator twice per successful prompt —
/// once inside the re-prompt hook, once after ShowAsync returns to
/// lift `string -> 'T`. The double cost is negligible at human typing
/// speeds (see research.md §R3 trade-off note).
type Validator<'T> = string -> Result<'T, string>

/// Internal accessor — visible only within this assembly.
/// Exposes label pre-flight for direct unit testing without a real TTY.
module internal Internal =

    /// Runs label pre-flight for a list of (value, Composition) choices.
    /// Returns Ok with projected (value, plain-text label) pairs on success,
    /// or Error on the first choice whose Composition fails to render.
    val preflightLabels :
        promptName: string ->
            choices: ('T * Composition) list ->
            Result<('T * string) list, RenderError>

module TextPrompt =

    /// Prompt for free-form text input, with typed validation.
    /// On invalid input, the underlying library re-prompts inline with
    /// the validator's error message (no exception thrown). On cancellation,
    /// returns `Error (UserCancelled "text-prompt")`. On non-TTY console,
    /// returns `Error (NoInteractiveConsole "text-prompt")` without blocking.
    val ask :
        console: Console ->
            title: Composition ->
            validator: Validator<'T> ->
            cancellation: System.Threading.CancellationToken ->
            Async<Result<'T, RenderError>>

module SelectionPrompt =

    /// Prompt for a single choice from a list. Choices are
    /// `('T * Composition)` pairs: `'T` is the value returned to the
    /// caller; `Composition` is the displayed label (lowered to
    /// plain-text plus structural padding/alignment — colour stripped
    /// per data-model.md §0.1).
    ///
    /// Validates `choices` non-empty before ShowAsync. Each choice label
    /// is pre-flighted via the label projection; any label that fails to
    /// render returns `Error (InvalidArgument ("SelectionPrompt", _))`.
    ///
    /// The `'T: not null` constraint is propagated from the underlying
    /// prompt type's generic parameter requirement.
    val ask :
        console: Console ->
            title: Composition ->
            choices: ('T * Composition) list ->
            cancellation: System.Threading.CancellationToken ->
            Async<Result<'T, RenderError>>
        when 'T: not null

module MultiSelectionPrompt =

    /// Prompt for zero or more choices from a list. Empty selection
    /// (user confirms with no items checked) returns `Ok []` — not an
    /// error; multi-selection naturally permits the empty subset.
    /// Other validation rules identical to `SelectionPrompt.ask`.
    ///
    /// The `'T: not null` constraint is propagated from the underlying
    /// prompt type's generic parameter requirement.
    val ask :
        console: Console ->
            title: Composition ->
            choices: ('T * Composition) list ->
            cancellation: System.Threading.CancellationToken ->
            Async<Result<'T list, RenderError>>
        when 'T: not null

module ConfirmationPrompt =

    /// Yes/No prompt with a default choice (returned if the user
    /// presses Enter without typing anything).
    val ask :
        console: Console ->
            title: Composition ->
            defaultChoice: bool ->
            cancellation: System.Threading.CancellationToken ->
            Async<Result<bool, RenderError>>
