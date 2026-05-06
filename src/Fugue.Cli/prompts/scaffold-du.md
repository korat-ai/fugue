---
command: scaffold-du
args: [concept]
description: Generate F# DU with match examples and JSON attrs
---
Generate a complete F# discriminated union for the concept "{concept}".

Include:
1. The DU definition with well-named cases and meaningful data payloads.
2. An exhaustive `match` example covering every case.
3. Smart constructor helpers (active patterns or module functions) for common construction patterns.
4. `[<JsonDerivedType>]` / `[<JsonPolymorphic>]` attributes for AOT-safe System.Text.Json serialization.
5. A brief comment per case explaining its semantics.

Target: F# 9 / .NET 10, Native AOT compatible.
