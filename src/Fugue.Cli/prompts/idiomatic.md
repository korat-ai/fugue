---
command: idiomatic
args: [arg]
description: Suggest idiomatic style improvements for the language
---
Review "{arg}" for idiomatic style improvements.

For F#: flag non-idiomatic patterns and suggest pipe-based, DU-based, or computation-expression rewrites.
For Elixir: flag imperative loops (use Enum/Stream pipelines instead) and missing pattern match clauses.
For Kotlin: flag nullable handling that should use `?.let`/`?:` and unnecessary `!!`.
For TypeScript: flag mutation where `const` + spread is preferred and callback nesting that should be Promise chains.

For each suggestion: quote the original, show the idiomatic alternative, and explain why it's preferred.

Read the file first, then provide structured feedback.
