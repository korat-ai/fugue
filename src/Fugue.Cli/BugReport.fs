module Fugue.Cli.BugReport

open System
open System.Text
open System.Text.RegularExpressions

type TurnRecord = {
    UserPrompt : string
    ToolCalls  : (string * string * string) list  // (toolName, argsJson, resultText)
    AiResponse : string
    ErrorText  : string option
}

let private bearerRx  = Regex(@"(Bearer\s+|sk-)[A-Za-z0-9\-_]{16,}", RegexOptions.Compiled)
let private apiKeyRx  = Regex(@"(api[-_]?key\s*[:=]\s*)[""']?[A-Za-z0-9\-_]{16,}[""']?", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)
let private anthropicRx = Regex(@"sk-ant-[A-Za-z0-9\-_]{20,}", RegexOptions.Compiled)

let private redact (projectRoot: string) (text: string) =
    let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
    let t1 = anthropicRx.Replace(text, "[REDACTED]")
    let t2 = bearerRx.Replace(t1, "$1[REDACTED]")
    let t3 = apiKeyRx.Replace(t2, "$1[REDACTED]")
    t3.Replace(home, "~").Replace(projectRoot, "<project>")

let format (fugueVersion: string) (os: string) (provider: string) (model: string) (projectRoot: string) (turn: TurnRecord) : string =
    let sb = StringBuilder()
    let r  = redact projectRoot
    sb.AppendLine "## Environment"   |> ignore
    sb.AppendLine(sprintf "- Fugue: `%s`" fugueVersion) |> ignore
    sb.AppendLine(sprintf "- OS: `%s`"    os)           |> ignore
    sb.AppendLine(sprintf "- Provider/Model: `%s` / `%s`" provider model) |> ignore
    sb.AppendLine() |> ignore
    sb.AppendLine "## User prompt" |> ignore
    sb.AppendLine(sprintf "```\n%s\n```" (r turn.UserPrompt)) |> ignore
    sb.AppendLine() |> ignore
    if turn.ToolCalls.Length > 0 then
        sb.AppendLine "## Tool calls" |> ignore
        for (name, args, result) in turn.ToolCalls do
            sb.AppendLine(sprintf "**%s** args: `%s`" name (r args)) |> ignore
            sb.AppendLine(sprintf "<details><summary>Result</summary>\n\n```\n%s\n```\n</details>" (r result)) |> ignore
            sb.AppendLine() |> ignore
    match turn.ErrorText with
    | Some err ->
        sb.AppendLine "## Error" |> ignore
        sb.AppendLine(sprintf "```\n%s\n```" (r err)) |> ignore
    | None -> ()
    sb.AppendLine() |> ignore
    sb.AppendLine "## AI response (truncated to 500 chars)" |> ignore
    let trunc = if turn.AiResponse.Length > 500 then turn.AiResponse.[..499] + "…" else turn.AiResponse
    sb.AppendLine(sprintf "```\n%s\n```" (r trunc)) |> ignore
    sb.ToString()
