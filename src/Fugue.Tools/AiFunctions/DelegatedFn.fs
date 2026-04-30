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
            match Fugue.Tools.CircuitBreaker.check name with
            | Some reason ->
                return box ("[circuit-open] " + reason)
            | None ->
                let mutable captured : System.Runtime.ExceptionServices.ExceptionDispatchInfo option = None
                let mutable successResult : obj | null = null
                let mutable circuitOpenMsg : string option = None
                try
                    let! result = invoke args ct
                    Fugue.Tools.CircuitBreaker.recordSuccess name
                    successResult <- box result
                with ex ->
                    let opened = Fugue.Tools.CircuitBreaker.recordFailure name
                    if opened then
                        circuitOpenMsg <- Some (sprintf "[circuit-open] %s disabled after repeated failures. Last error: %s" name ex.Message)
                    else
                        captured <- Some (System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture ex)
                match circuitOpenMsg with
                | Some msg -> return box msg
                | None ->
                    match captured with
                    | Some edi -> edi.Throw(); return null
                    | None -> return successResult
        }
        ValueTask<obj | null>(task)

/// Parse a constant schema JSON string into a clone-safe JsonElement.
/// AOT-safe: JsonDocument.Parse(string) does not use reflection.
let parseSchema (json: string) : JsonElement =
    use doc = JsonDocument.Parse(json)
    doc.RootElement.Clone()
