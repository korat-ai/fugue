namespace Fugue.Adapters.Console

/// Typed failure cases returned across the adapter boundary.
/// Closed DU — no catch-all. Adding a case is a v-MINOR change; renaming
/// or removing one is v-MAJOR. See data-model.md §1.
type RenderError =
    | InvalidMarkup    of input: string * detail: string
    | InvalidStyleSpec of spec: string * detail: string
    | DegenerateWidth  of reportedWidth: int
    | EmptyComposition of node: string
    /// Caller-supplied argument failed structural validation in a smart
    /// constructor (e.g. negative padding, ratio > 1.0, ragged table row).
    /// Distinct from `RenderFailed` which means Spectre threw at render time.
    | InvalidArgument  of node: string * detail: string
    | RenderFailed     of primitive: string * message: string
    /// A user-initiated cancellation interrupted the operation (e.g. via CancellationToken).
    | UserCancelled        of operation: string
    /// The operation requires an interactive console (TTY) but none is available.
    | NoInteractiveConsole of operation: string
    /// A concurrent live session is already running on this console.
    | ConcurrentLiveSession of console: string
