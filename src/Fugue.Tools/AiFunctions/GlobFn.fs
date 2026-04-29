module Fugue.Tools.AiFunctions.GlobFn

open Microsoft.Extensions.AI
open Fugue.Tools.AiFunctions

let private schema = DelegatedFn.parseSchema """{
  "type":"object",
  "properties":{
    "pattern":{"type":"string","description":"Glob pattern (e.g. **/*.fs)"},
    "root":   {"type":"string","description":"Optional root directory; defaults to cwd"}
  },
  "required":["pattern"]
}"""

let create (cwd: string) : AIFunction =
    DelegatedFn.DelegatedAIFunction(
        name        = "Glob",
        description = "Find files matching a glob pattern, sorted by mtime descending.",
        schema      = schema,
        invoke      = fun args ct -> task {
            ct.ThrowIfCancellationRequested()
            let pattern = Args.getStr    args "pattern"
            let root    = Args.tryGetStr args "root"
            return Fugue.Tools.GlobTool.glob cwd pattern root
        }) :> AIFunction
