---
command: rop
args: [desc]
description: Generate ROP Result/Either chain from plain-English
---
Generate a railway-oriented programming (ROP) pipeline for the following operation:

"{desc}"

Requirements:
1. Use F# `Result<'ok, 'err>` types (or adapt for the target language).
2. Define a discriminated union for all possible error cases.
3. Compose steps with `Result.bind` (or `>>=`) — no exceptions, no `try/catch`.
4. Unify error types across steps; if types differ, map to the common error DU.
5. Include brief inline comments explaining each step.
6. Return only the code (no prose preamble).
