module Fugue.Tools.AiFunctions.GrepFn

open Microsoft.Extensions.AI
open Fugue.Tools.AiFunctions

let private schema = DelegatedFn.parseSchema """{
  "type":"object",
  "properties":{
    "pattern":{"type":"string","description":"Regex pattern"},
    "root":   {"type":"string","description":"Optional root directory"},
    "glob":   {"type":"string","description":"Optional file glob filter"},
    "symbol": {"type":"boolean","description":"Word-boundary symbol search, skip comment lines"}
  },
  "required":["pattern"]
}"""

let create (cwd: string) (hooksConfig: Fugue.Core.Hooks.HooksConfig) (sessionId: string) : AIFunction =
    DelegatedFn.DelegatedAIFunction(
        name        = "Grep",
        description = "Regex-search files. Returns path:line:text matches.",
        schema      = schema,
        hooksConfig = hooksConfig,
        sessionId   = sessionId,
        invoke      = fun args ct -> task {
            ct.ThrowIfCancellationRequested()
            let pattern = Args.getStr    args "pattern"
            let root    = Args.tryGetStr args "root"
            let glob    = Args.tryGetStr args "glob"
            let symbol  = Args.tryGetBool args "symbol"
            return Fugue.Tools.GrepTool.grep cwd pattern root glob symbol
        }) :> AIFunction
