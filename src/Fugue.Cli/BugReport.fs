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
    sb.AppendLine($"- Fugue: `{fugueVersion}`") |> ignore
    sb.AppendLine($"- OS: `{os}`")              |> ignore
    sb.AppendLine($"- Provider/Model: `{provider}` / `{model}`") |> ignore
    sb.AppendLine() |> ignore
    sb.AppendLine "## User prompt" |> ignore
    sb.AppendLine($"```\n{r turn.UserPrompt}\n```") |> ignore
    sb.AppendLine() |> ignore
    if turn.ToolCalls.Length > 0 then
        sb.AppendLine "## Tool calls" |> ignore
        for (name, args, result) in turn.ToolCalls do
            sb.AppendLine($"**{name}** args: `{r args}`") |> ignore
            sb.AppendLine($"<details><summary>Result</summary>\n\n```\n{r result}\n```\n</details>") |> ignore
            sb.AppendLine() |> ignore
    match turn.ErrorText with
    | Some err ->
        sb.AppendLine "## Error" |> ignore
        sb.AppendLine($"```\n{r err}\n```") |> ignore
    | None -> ()
    sb.AppendLine() |> ignore
    sb.AppendLine "## AI response (truncated to 500 chars)" |> ignore
    let trunc = if turn.AiResponse.Length > 500 then turn.AiResponse.[..499] + "…" else turn.AiResponse
    sb.AppendLine($"```\n{r trunc}\n```") |> ignore
    sb.ToString()
