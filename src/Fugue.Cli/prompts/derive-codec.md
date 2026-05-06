---
command: derive-codec
args: [typeName]
description: Generate AOT-safe STJ JsonSerializerContext
---
Generate an AOT-safe JSON codec for the F# type "{typeName}".

1. Read the type definition from the codebase (use the Read or Grep tools).
2. Generate a `[<JsonSerializable>]` `JsonSerializerContext` subclass covering the type and any nested types.
3. Add `[<JsonPropertyName>]` attributes for all fields to ensure stable serialization.
4. If the type is a DU, add `[<JsonPolymorphic>]` and `[<JsonDerivedType>]` for each case.
5. Show a usage example: `JsonSerializer.Serialize(value, MyContext.Default.MyType)`.

Target: System.Text.Json source generation, .NET 10, Native AOT compatible.
