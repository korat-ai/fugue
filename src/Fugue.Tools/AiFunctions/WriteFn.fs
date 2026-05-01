module Fugue.Tools.AiFunctions.WriteFn

open Microsoft.Extensions.AI
open Fugue.Tools.AiFunctions

let private schema = DelegatedFn.parseSchema """{
  "type":"object",
  "properties":{
    "path":   {"type":"string","description":"Absolute or cwd-relative file path"},
    "content":{"type":"string","description":"Full text content to write"}
  },
  "required":["path","content"]
}"""

let create (cwd: string) (hooksConfig: Fugue.Core.Hooks.HooksConfig) (sessionId: string) : AIFunction =
    DelegatedFn.DelegatedAIFunction(
        name        = "Write",
        description = "Write text content to a file. Overwrites if exists.",
        schema      = schema,
        hooksConfig = hooksConfig,
        sessionId   = sessionId,
        invoke      = fun args ct -> task {
            ct.ThrowIfCancellationRequested()
            let path    = Args.getStr args "path"
            let content = Args.getStr args "content"
            return! Fugue.Tools.RetryPolicy.retryAsync ct (fun () ->
                System.Threading.Tasks.Task.FromResult(Fugue.Tools.WriteTool.write cwd path content))
        }) :> AIFunction
