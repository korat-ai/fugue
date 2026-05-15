namespace Fugue.Adapters.Console

/// Typed failure cases returned across the adapter boundary.
/// Closed DU — no catch-all. Adding a case is a v-MINOR change; renaming
/// or removing one is v-MAJOR. See data-model.md §1.
type RenderError =
    | InvalidMarkup    of input: string * detail: string
    | InvalidStyleSpec of spec: string * detail: string
    | DegenerateWidth  of reportedWidth: int
    | EmptyComposition of node: string
    | RenderFailed     of primitive: string * message: string
