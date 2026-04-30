module Fugue.Tools.AiFunctions.BashFn

open Microsoft.Extensions.AI
open Fugue.Tools.AiFunctions

let private schema = DelegatedFn.parseSchema """{
  "type":"object",
  "properties":{
    "command":    {"type":"string","description":"Shell command to run via /bin/zsh -lc"},
    "timeout_ms": {"type":"integer","description":"Timeout in milliseconds (default 60000 = 60s). Process killed after timeout."}
  },
  "required":["command"]
}"""

let create (cwd: string) : AIFunction =
    DelegatedFn.DelegatedAIFunction(
        name        = "Bash",
        description = "Run a shell command via /bin/zsh -lc.",
        schema      = schema,
        invoke      = fun args ct -> task {
            ct.ThrowIfCancellationRequested()
            let command   = Args.getStr     args "command"
            let timeoutMs = Args.tryGetInt  args "timeout_ms"
            return Fugue.Tools.BashTool.bash cwd command timeoutMs
        }) :> AIFunction
