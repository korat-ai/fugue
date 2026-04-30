module Fugue.Tools.AiFunctions.ReadFn

open Microsoft.Extensions.AI
open Fugue.Tools.AiFunctions

let private schema = DelegatedFn.parseSchema """{
  "type":"object",
  "properties":{
    "path":  {"type":"string","description":"Absolute or cwd-relative file path"},
    "offset":{"type":"integer","description":"1-based start line. For files over 200 lines always specify offset+limit to read only the relevant section."},
    "limit": {"type":"integer","description":"Max lines to read. Prefer 100-200 for large files. Omit only for small files (<200 lines)."}
  },
  "required":["path"]
}"""

let create (cwd: string) : AIFunction =
    DelegatedFn.DelegatedAIFunction(
        name        = "Read",
        description = "Read a text file. Returns lines prefixed with 1-based line numbers. For files >200 lines use offset+limit to read only what you need.",
        schema      = schema,
        invoke      = fun args ct -> task {
            ct.ThrowIfCancellationRequested()
            let path   = Args.getStr     args "path"
            let offset = Args.tryGetInt  args "offset"
            let limit  = Args.tryGetInt  args "limit"
            return Fugue.Tools.ReadTool.read cwd path offset limit
        }) :> AIFunction
