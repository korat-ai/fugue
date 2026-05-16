namespace Fugue.Adapters.Console

/// Escape-tracked user input. Private DU constructor prevents accidental
/// double-escaping or skipped escaping. See data-model.md §3.
type SafeText = private SafeText of string

module SafeText =

    /// Wrap arbitrary user input. Applies `Markup.Escape` exactly once.
    /// NOT idempotent — never call on already-escaped content (double-escapes).
    /// Use `ofLiteral` for strings already known to be markup-free.
    val ofUser: raw: (string | null) -> SafeText

    /// Wrap a string the caller has asserted is already escaped / markup-free.
    /// Use sparingly — `ofUser` is the safe default.
    val ofLiteral: already: (string | null) -> SafeText

    /// Read out the escaped string (for renderers / tests).
    val unwrap: SafeText -> string
