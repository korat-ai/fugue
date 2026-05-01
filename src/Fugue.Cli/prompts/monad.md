---
command: monad
args: [desc]
description: Generate F# CE / Scala for-comprehension from pseudocode
---
Convert the following imperative description into idiomatic monadic F# code:

"{desc}"

Steps:
1. Identify the primary effect (async I/O, error handling, sequence generation, cancellation, …).
2. Choose the correct F# computation expression (`task`, `async`, `result`, `asyncResult`, `seq`, …).
3. Generate the CE block — no nested `match`/`if` for error handling; use `let!` and `return`.
4. If the operation mixes effects (e.g. async + Result), compose them correctly (e.g. `taskResult`).
5. Include the type signatures.

Return only the code with a one-line comment on the CE chosen.
