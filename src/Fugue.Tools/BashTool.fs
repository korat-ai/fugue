module Fugue.Tools.BashTool

open System
open System.Diagnostics
open System.Text
open System.Threading
open System.ComponentModel

[<Description("Run a shell command via /bin/zsh -lc and return combined stdout/stderr with exit code.")>]
let bash
    ([<Description("Working directory.")>] cwd: string)
    ([<Description("Command line to execute (shell syntax allowed).")>] command: string)
    ([<Description("Timeout in milliseconds (default 60000 = 60s).")>] timeoutMs: int option)
    ([<Description("CancellationToken to interrupt the process early.")>] ct: CancellationToken)
    : string =
    let timeout = timeoutMs |> Option.defaultValue 60_000

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

    // Register cancellation: kill process tree if token is cancelled before timeout.
    use _cancelReg =
        ct.Register(fun () ->
            try proc.Kill(entireProcessTree = true) with _ -> ()
            if not (proc.WaitForExit 3000) then
                try proc.Kill() with _ -> ())

    let finished = proc.WaitForExit timeout
    if ct.IsCancellationRequested then
        "<cancelled>\nstdout:\n" + string stdout + "\nstderr:\n" + string stderr
    elif not finished then
        // SIGTERM the process tree first, then escalate to SIGKILL after 3s
        try proc.Kill(entireProcessTree = true) with _ -> ()
        if not (proc.WaitForExit 3000) then
            try proc.Kill() with _ -> ()
        "<timeout> after " + string timeout + " ms\nstdout:\n" + string stdout + "\nstderr:\n" + string stderr
    else
        proc.WaitForExit()
        "exit_code=" + string proc.ExitCode + "\nstdout:\n" + string stdout + "\nstderr:\n" + string stderr
