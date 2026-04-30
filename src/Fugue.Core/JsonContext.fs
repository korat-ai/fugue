module Fugue.Core.JsonContext

open System.Diagnostics.CodeAnalysis
open System.Text.Json
open System.Text.Json.Serialization

// DynamicallyAccessedMembers preserves the parameterless constructor (from [<CLIMutable>])
// and all public properties for STJ reflection-based (de)serialization under AOT.
[<CLIMutable; DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)>]
type UiConfigDto = {
    userAlignment : string | null
    locale        : string | null
    promptTemplate: string | null
    bell          : bool
    theme         : string | null
    emojiMode     : string | null
    bubblesMode   : bool
}

[<CLIMutable; DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)>]
type AppConfigDto =
    { provider: string
      model: string
      apiKey: string
      ollamaEndpoint: string
      baseUrl: string | null
      maxIterations: int
      maxTokens: int
      ui: UiConfigDto | null }

/// Shared JsonSerializerOptions for AppConfigDto serialization.
/// NOTE: F# does not support JsonSerializerContext source generation the same
/// way C# does — abstract members would need manual implementation.
/// Using JsonSerializerOptions with camelCase policy achieves the same goal.
let appConfigOptions =
    let opts = JsonSerializerOptions()
    opts.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
    opts.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
    opts
