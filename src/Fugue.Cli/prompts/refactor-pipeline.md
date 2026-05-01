---
command: refactor-pipeline
args: [label]
description: Rewrite F# let-bindings as |> pipe chain
---
Refactor the following F# code into an idiomatic pipe chain (`|>`).

Rules:
1. Identify a linear data-flow sequence where each binding feeds into the next.
2. Replace the sequence with a single `|>` chain.
3. Use partial application where possible; inline lambdas only when necessary.
4. Preserve all semantics exactly — no behaviour changes.
5. Return only the refactored code block.

Source ({label}):
```fsharp
{code}
```
