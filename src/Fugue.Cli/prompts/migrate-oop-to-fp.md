---
command: migrate-oop-to-fp
args: [file]
description: Convert OOP class hierarchy to F# DUs
---
Migrate the following OOP code to idiomatic F#:

```
{code}
```

Migration rules:
1. `abstract class` / `interface` → F# discriminated union (one case per concrete class).
2. Mutable fields → immutable record fields; setters → `with` copy-and-update expressions.
3. `null` returns → `Option<T>`; `throw` → `Result<T, Error>` or DU error case.
4. Virtual method dispatch → exhaustive `match` on the DU.
5. Constructor logic → module-level factory functions.
6. Preserve all business logic exactly.

Return the complete F# translation with brief migration notes per section.
