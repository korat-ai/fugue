namespace Fugue.Adapters.Console

/// Escape-tracked user input. Private DU constructor prevents accidental
/// double-escaping or skipped escaping. See data-model.md §3.
type SafeText = private SafeText of string

module SafeText =
    /// Wrap arbitrary user input. Applies `Markup.Escape` exactly once.
    /// Idempotent: `ofUser (unwrap (ofUser s)) = ofUser s`.
    let ofUser (raw: string | null) : SafeText =
        let s =
            match raw with
            | null -> ""
            | r    -> r
        SafeText (Spectre.Console.Markup.Escape s)

    /// Wrap a string the caller has asserted is already escaped / markup-free.
    /// Use sparingly — `ofUser` is the safe default.
    let ofLiteral (already: string | null) : SafeText =
        let s =
            match already with
            | null -> ""
            | a    -> a
        SafeText s

    /// Read out the escaped string (for renderers / tests).
    let unwrap (SafeText s) : string = s
