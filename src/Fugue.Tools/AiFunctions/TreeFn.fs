module Fugue.Tools.AiFunctions.TreeFn

open Microsoft.Extensions.AI
open Fugue.Tools.AiFunctions

let private schema = DelegatedFn.parseSchema """{
  "type":"object",
  "properties":{
    "path":  {"type":"string","description":"Directory path (absolute or cwd-relative). Defaults to '.' if omitted."},
    "depth": {"type":"integer","description":"Max depth to recurse. Default 3."},
    "glob":  {"type":"string","description":"Filename pattern to filter (e.g. '*.fs'). Optional."}
  },
  "required":[]
}"""

let create (cwd: string) (hooksConfig: Fugue.Core.Hooks.HooksConfig) (sessionId: string) : AIFunction =
    DelegatedFn.DelegatedAIFunction(
        name        = "Tree",
        description = "Show directory structure as an ASCII tree. Optionally filter by glob pattern.",
        schema      = schema,
        hooksConfig = hooksConfig,
        sessionId   = sessionId,
        invoke      = fun args ct -> task {
            ct.ThrowIfCancellationRequested()
            let path  = Args.tryGetStr args "path"  |> Option.defaultValue "."
            let depth = Args.tryGetInt  args "depth" |> Option.defaultValue 3
            let glob  = Args.tryGetStr args "glob"
            return Fugue.Tools.TreeTool.tree cwd path depth glob
        }) :> AIFunction
