module Fugue.Core.JsonContext

open System.Text.Json
open System.Text.Json.Serialization

[<CLIMutable>]
type UiConfigDto = {
    userAlignment: string | null
    locale: string | null
}

[<CLIMutable>]
type AppConfigDto =
    { provider: string
      model: string
      apiKey: string
      ollamaEndpoint: string
      maxIterations: int
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
