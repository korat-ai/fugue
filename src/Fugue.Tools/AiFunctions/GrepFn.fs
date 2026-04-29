module Fugue.Tools.AiFunctions.GrepFn

open Microsoft.Extensions.AI
open Fugue.Tools.AiFunctions

let private schema = DelegatedFn.parseSchema """{
  "type":"object",
  "properties":{
    "pattern":{"type":"string","description":"Regex pattern"},
    "root":   {"type":"string","description":"Optional root directory"},
    "glob":   {"type":"string","description":"Optional file glob filter"}
  },
  "required":["pattern"]
}"""

let create (cwd: string) : AIFunction =
    DelegatedFn.DelegatedAIFunction(
        name        = "Grep",
        description = "Regex-search files. Returns path:line:text matches.",
        schema      = schema,
        invoke      = fun args ct -> task {
            ct.ThrowIfCancellationRequested()
            let pattern = Args.getStr    args "pattern"
            let root    = Args.tryGetStr args "root"
            let glob    = Args.tryGetStr args "glob"
            return Fugue.Tools.GrepTool.grep cwd pattern root glob
        }) :> AIFunction
