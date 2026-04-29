module Fugue.Tools.AiFunctions.DelegatedFn

open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI

/// Single concrete AIFunction subclass parametrised by name/desc/schema/body.
/// All six tools instantiate this with different bodies — no subclass per tool.
/// AOT-clean: no reflection on parameter types.
type DelegatedAIFunction(
        name: string,
        description: string,
        schema: JsonElement,
        invoke: AIFunctionArguments -> CancellationToken -> Task<string>) =
    inherit AIFunction()

    override _.Name        = name
    override _.Description = description
    override _.JsonSchema  = schema
    override _.InvokeCoreAsync(args: AIFunctionArguments, ct: CancellationToken) =
        let task = task {
            let! result = invoke args ct
            return box result
        }
        ValueTask<obj | null>(task)

/// Parse a constant schema JSON string into a clone-safe JsonElement.
/// AOT-safe: JsonDocument.Parse(string) does not use reflection.
let parseSchema (json: string) : JsonElement =
    use doc = JsonDocument.Parse(json)
    doc.RootElement.Clone()
