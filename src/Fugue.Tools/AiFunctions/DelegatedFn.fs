module Fugue.Tools.AiFunctions.DelegatedFn

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI

/// Serialize AIFunctionArguments to a compact JSON object string.
/// AOT-safe: uses JsonElement.GetRawText() and manual string escaping — no reflection.
let private argsToJson (args: AIFunctionArguments) : string =
    let esc (s: string) =
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r")
    let pairs =
        [ for kvp in args do
            let valueStr =
                match kvp.Value |> Option.ofObj with
                | None -> "null"
                | Some v ->
                    match v with
                    | :? JsonElement as el -> el.GetRawText()
                    | :? string as s -> "\"" + esc s + "\""
                    | other ->
                        let s = other.ToString() |> Option.ofObj |> Option.defaultValue ""
                        "\"" + esc s + "\""
            yield "\"" + esc kvp.Key + "\":" + valueStr ]
    "{" + String.concat "," pairs + "}"

/// Single concrete AIFunction subclass parametrised by name/desc/schema/body.
/// All six tools instantiate this with different bodies — no subclass per tool.
/// AOT-clean: no reflection on parameter types.
type DelegatedAIFunction(
        name: string,
        description: string,
        schema: JsonElement,
        hooksConfig: Fugue.Core.Hooks.HooksConfig,
        sessionId: string,
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
                // ── PreToolUse ────────────────────────────────────────────
                let argsJson = argsToJson args
                let! preResult = Fugue.Core.Hooks.runPreToolUse name argsJson sessionId hooksConfig
                match preResult with
                | Fugue.Core.Hooks.PreToolResult.Block reason ->
                    return box ("[hook-blocked] " + reason)
                | _ ->
                    // Apply arg modifications if any
                    match preResult with
                    | Fugue.Core.Hooks.PreToolResult.ModifyArgs changes ->
                        for (k, v) in changes do
                            use doc = JsonDocument.Parse(v)
                            args[k] <- box (doc.RootElement.Clone())
                    | _ -> ()
                    // ── Invoke ────────────────────────────────────────────
                    let mutable captured : System.Runtime.ExceptionServices.ExceptionDispatchInfo option = None
                    let mutable successResult : obj | null = null
                    let mutable circuitOpenMsg : string option = None
                    let mutable toolResultStr = ""
                    let mutable toolIsError = false
                    try
                        let! result = invoke args ct
                        Fugue.Tools.CircuitBreaker.recordSuccess name
                        successResult <- box result
                        toolResultStr <- result
                    with ex ->
                        let opened = Fugue.Tools.CircuitBreaker.recordFailure name
                        toolIsError <- true
                        toolResultStr <- ex.Message
                        if opened then
                            circuitOpenMsg <- Some ("[circuit-open] " + name + " disabled after repeated failures. Last error: " + ex.Message)
                        else
                            captured <- Some (System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture ex)
                    // ── PostToolUse ───────────────────────────────────────
                    let postArgsJson = argsToJson args
                    Fugue.Core.Hooks.runPostToolUse name postArgsJson toolResultStr toolIsError sessionId hooksConfig
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
