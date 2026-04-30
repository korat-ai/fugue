module Fugue.Tools.AiFunctions.EditFn

open Microsoft.Extensions.AI
open Fugue.Tools.AiFunctions

let private schema = DelegatedFn.parseSchema """{
  "type":"object",
  "properties":{
    "path":        {"type":"string","description":"File path"},
    "old_string":  {"type":"string","description":"Exact text to replace"},
    "new_string":  {"type":"string","description":"Replacement text"},
    "replace_all": {"type":"boolean","description":"Replace every occurrence (default false — must be unique)"}
  },
  "required":["path","old_string","new_string"]
}"""

let create (cwd: string) : AIFunction =
    DelegatedFn.DelegatedAIFunction(
        name        = "Edit",
        description = "Edit a text file by exact-string replacement.",
        schema      = schema,
        invoke      = fun args ct -> task {
            ct.ThrowIfCancellationRequested()
            let path       = Args.getStr     args "path"
            let oldStr     = Args.getStr     args "old_string"
            let newStr     = Args.getStr     args "new_string"
            let replaceAll = Args.tryGetBool args "replace_all"
            return! Fugue.Tools.RetryPolicy.retryAsync ct (fun () ->
                System.Threading.Tasks.Task.FromResult(Fugue.Tools.EditTool.edit cwd path oldStr newStr replaceAll))
        }) :> AIFunction
