---
command: typegen
args: [signature_and_description]
description: generate a function body that satisfies a declared type signature, then verify with the build
---

The user wants you to generate a function body that satisfies a declared
type signature. Treat the signature as a hard contract, not a hint.

User input: {signature_and_description}

The input is in the form `<signature> :: <what it should do>` or, if no
`::` separator is present, the entire string is the signature plus an
implied "implement this idiomatically" intent.

Workflow:

1. Parse the signature. Identify the language from cues (e.g., `let foo :
   int -> string` is F#, `def foo(...) -> ...` is Python, `fun foo(...): ...`
   is Kotlin/Scala). If ambiguous, default to the language of the current
   project (use Glob/Read to detect the predominant file extension under
   `src/`).

2. Use Glob and Read to discover the surrounding code: existing types
   referenced by the signature (records, DUs, classes, traits, etc.).
   You need their definitions to write a correct body.

3. Generate the function body. The body MUST type-check against the
   declared signature. Do not change the signature. Do not introduce new
   parameters. Do not weaken return types (e.g., turning `Result<T, E>`
   into `T option`).

4. Write the new code to disk via the Write/Edit tool. Choose a sensible
   file location based on the surrounding project layout — DO NOT invent
   a new top-level directory.

5. Run the project's build via the Bash tool. For F#: `dotnet build` from
   the relevant project root. For Scala/Kotlin/Java: `sbt compile` or
   `gradle build` or `mvn compile` depending on what's present. For
   TypeScript: `tsc --noEmit`.

6. If the build fails, read the compiler output, fix the body, write
   again, rebuild. Iterate up to 3 times.

7. Final response format:

   - One sentence stating the language and signature.
   - Fenced code block of the final function body (only the body — not
     surrounding scaffolding the user already had).
   - One short paragraph: build status (pass/fail after N iterations) and
     the file path where you wrote it.

Anti-patterns to avoid:
- Do NOT explain "what the function does" before writing it; the user
  already knows.
- Do NOT add commentary inside the body that restates the type contract.
- Do NOT invent input examples; the signature is the spec.
- Do NOT propose multiple implementations — pick one and verify it.
