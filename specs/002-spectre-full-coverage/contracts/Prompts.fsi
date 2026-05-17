// =============================================================================
// PHASE 1 CONTRACT SKETCH — NOT THE FINAL `.fsi` IN src/.
//
// Feature:  002-spectre-full-coverage
// User Story: P4 (Interactive prompts)
//
// Async F# wrappers over Spectre's four prompt types. Per research.md
// §R3 and data-model.md §5:
//   * Return shape:     Async<Result<'T, RenderError>>
//   * Cancellation:     native CancellationToken passed to Spectre's
//                       ShowAsync, with a defensive Async wrap
//   * Choice labels:    Composition (lowered to plain-text via the
//                       projection from data-model.md §0.1; colour
//                       stripped to avoid Spectre highlight glitches)
//   * Validator:        typed F# `string -> Result<'T, string>`
//
// Invariants:
//   * No Spectre type appears in this .fsi (SC-004).
//   * Empty choice lists rejected at smart-constructor time (Error
//     EmptyComposition), BEFORE ShowAsync is invoked (FR-005).
//   * Non-TTY consoles short-circuit with Error NoInteractiveConsole
//     (FR-016) instead of blocking on stdin.
//   * Cancellation always surfaces as Error (UserCancelled "<op>")
//     (FR-013).
// =============================================================================

namespace Fugue.Adapters.Console

/// Typed validator passed to `TextPrompt.ask`. Returns `Ok` with the
/// parsed value on success, or `Error` with a user-visible message
/// to display in Spectre's native re-prompt loop. The wrapper invokes
/// the validator twice per successful prompt — once inside Spectre's
/// `Validate` hook for the re-prompt loop, once after `ShowAsync`
/// returns to lift `string -> 'T`. The double cost is negligible at
/// human typing speeds (see research.md §R3 trade-off note).
type Validator<'T> = string -> Result<'T, string>

module TextPrompt =

    /// Prompt for free-form text input, with typed validation.
    /// On invalid input, Spectre re-prompts inline with the validator's
    /// error message (no exception thrown). On cancellation, returns
    /// `Error (UserCancelled "text-prompt")`. On non-TTY console,
    /// returns `Error (NoInteractiveConsole "text-prompt")` without
    /// blocking on stdin.
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
    /// Validates `choices` non-empty BEFORE ShowAsync. Each choice's
    /// label is also pre-flighted via the label projection; any label
    /// that fails to render returns
    /// `Error (InvalidArgument ("SelectionPrompt", _))`.
    val ask :
        console: Console ->
            title: Composition ->
            choices: ('T * Composition) list ->
            cancellation: System.Threading.CancellationToken ->
            Async<Result<'T, RenderError>>

module MultiSelectionPrompt =

    /// Prompt for zero or more choices from a list. Empty selection
    /// (user confirms with no items checked) returns `Ok []` — not an
    /// error; multi-selection naturally permits the empty subset.
    /// Other validation rules identical to `SelectionPrompt.ask`.
    val ask :
        console: Console ->
            title: Composition ->
            choices: ('T * Composition) list ->
            cancellation: System.Threading.CancellationToken ->
            Async<Result<'T list, RenderError>>

module ConfirmationPrompt =

    /// Yes/No prompt with a default choice (returned if the user
    /// presses Enter without typing anything).
    val ask :
        console: Console ->
            title: Composition ->
            defaultChoice: bool ->
            cancellation: System.Threading.CancellationToken ->
            Async<Result<bool, RenderError>>
