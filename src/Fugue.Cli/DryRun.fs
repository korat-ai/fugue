module Fugue.Cli.DryRun

open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI

/// Wraps an AIFunction to log calls without executing (dry-run mode).
type DryRunFunction(inner: AIFunction) =
    inherit AIFunction()
    let mutable callCount = 0

    override _.Name = inner.Name
    override _.Description = inner.Description
    override _.JsonSchema = inner.JsonSchema

    override _.InvokeCoreAsync(args: AIFunctionArguments, ct: CancellationToken) =
        ct.ThrowIfCancellationRequested()
        callCount <- callCount + 1
        let argStr =
            try
                let sb = System.Text.StringBuilder()
                let mutable first = true
                for kvp in args do
                    if not first then sb.Append(", ") |> ignore
                    sb.Append(kvp.Key) |> ignore
                    sb.Append("=") |> ignore
                    match kvp.Value with
                    | :? JsonElement as el -> sb.Append(el.ToString()) |> ignore
                    | null -> sb.Append("null") |> ignore
                    | v -> sb.Append(string v) |> ignore
                    first <- false
                sb.ToString()
            with _ -> "…"
        let preview = sprintf "[dry-run] #%d %s(%s)" callCount inner.Name argStr
        ValueTask<obj | null>(Task.FromResult<obj | null>(box preview))

/// Wrap all tools for dry-run mode.
let wrapTools (tools: AIFunction list) : AIFunction list =
    tools |> List.map (fun t -> DryRunFunction(t) :> AIFunction)
