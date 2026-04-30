module Fugue.Tools.AiFunctions.WriteBatchFn

open Microsoft.Extensions.AI
open Fugue.Tools.AiFunctions

let private schema = DelegatedFn.parseSchema """{
  "type":"object",
  "properties":{
    "files":{"type":"string","description":"JSON array of {path, content} objects"}
  },
  "required":["files"]
}"""

let create (cwd: string) : AIFunction =
    DelegatedFn.DelegatedAIFunction(
        name        = "WriteBatch",
        description = "Atomically write multiple files in one operation (all-or-nothing).",
        schema      = schema,
        invoke      = fun args ct -> task {
            ct.ThrowIfCancellationRequested()
            let files = Args.getStr args "files"
            return Fugue.Tools.WriteBatchTool.writeBatch cwd files
        }) :> AIFunction
