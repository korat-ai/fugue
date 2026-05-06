module Fugue.Tools.AiFunctions.BashFn

open Microsoft.Extensions.AI
open Fugue.Tools.AiFunctions

let private schema = DelegatedFn.parseSchema """{
  "type":"object",
  "properties":{
    "command":    {"type":"string","description":"Shell command to run via /bin/sh -c"},
    "timeout_ms": {"type":"integer","description":"Timeout in milliseconds (default 60000 = 60s). Process killed after timeout."},
    "clean_env":  {"type":"boolean","description":"Strip secret env vars (keys, tokens, passwords) before running."}
  },
  "required":["command"]
}"""

let create (cwd: string) (hooksConfig: Fugue.Core.Hooks.HooksConfig) (sessionId: string) : AIFunction =
    DelegatedFn.DelegatedAIFunction(
        name        = "Bash",
        description = "Run a shell command via /bin/sh -c. Returns stdout, stderr, and exit code.",
        schema      = schema,
        hooksConfig = hooksConfig,
        sessionId   = sessionId,
        invoke      = fun args ct -> task {
            ct.ThrowIfCancellationRequested()
            let command   = Args.getStr    args "command"
            let timeoutMs = Args.tryGetInt args "timeout_ms"
            let cleanEnv  = Args.tryGetBool args "clean_env"
            let findings  = Fugue.Tools.InjectionGuard.checkBash command
            let blocks    = findings |> List.filter (fun f -> f.Severity = Fugue.Tools.InjectionGuard.Block)
            let warns     = findings |> List.filter (fun f -> f.Severity = Fugue.Tools.InjectionGuard.Warn)
            if not blocks.IsEmpty then
                let details = blocks |> List.map (fun f -> "  • " + f.Detail) |> String.concat "\n"
                return "[injection-guard] Blocked — dangerous pattern detected:\n" + details
            else
                let warnPrefix =
                    if warns.IsEmpty then ""
                    else
                        let details = warns |> List.map (fun f -> "  • " + f.Detail) |> String.concat "\n"
                        "[injection-guard] Warning — suspicious pattern:\n" + details + "\n\n"
                let result = Fugue.Tools.BashTool.bash cwd command timeoutMs cleanEnv ct
                return warnPrefix + result
        }) :> AIFunction
