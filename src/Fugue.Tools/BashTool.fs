module Fugue.Tools.BashTool

open System
open System.Diagnostics
open System.Text
open System.ComponentModel

[<Description("Run a shell command via /bin/zsh -lc and return combined stdout/stderr with exit code.")>]
let bash
    ([<Description("Working directory.")>] cwd: string)
    ([<Description("Command line to execute (shell syntax allowed).")>] command: string)
    ([<Description("Timeout in milliseconds (default 120000).")>] timeoutMs: int option)
    : string =
    let timeout = timeoutMs |> Option.defaultValue 120_000

    let psi = ProcessStartInfo("/bin/zsh", "-lc " + "\"" + command.Replace("\"", "\\\"") + "\"")
    psi.WorkingDirectory <- cwd
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false

    let proc =
        match Process.Start psi |> Option.ofObj with
        | Some p -> p
        | None -> failwith "Process.Start returned null"

    use _ = proc
    let stdout = StringBuilder()
    let stderr = StringBuilder()
    proc.OutputDataReceived.Add(fun e -> if not (isNull e.Data) then stdout.AppendLine e.Data |> ignore)
    proc.ErrorDataReceived.Add(fun e -> if not (isNull e.Data) then stderr.AppendLine e.Data |> ignore)
    proc.BeginOutputReadLine()
    proc.BeginErrorReadLine()

    let finished = proc.WaitForExit timeout
    if not finished then
        try proc.Kill(true) with _ -> ()
        sprintf "<timeout> after %d ms\nstdout:\n%s\nstderr:\n%s" timeout (string stdout) (string stderr)
    else
        proc.WaitForExit()
        sprintf "exit_code=%d\nstdout:\n%s\nstderr:\n%s" proc.ExitCode (string stdout) (string stderr)
