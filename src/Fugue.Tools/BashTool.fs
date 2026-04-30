module Fugue.Tools.BashTool

open System
open System.Diagnostics
open System.Text
open System.Threading
open System.ComponentModel

/// Patterns for secret-like env var names (suffix-based).
let private secretSuffixes =
    [| "_KEY"; "_SECRET"; "_TOKEN"; "_PASSWORD"; "_PASS"; "_CREDENTIAL"; "_CREDENTIALS" |]

/// Prefixes that are always stripped.
let private secretPrefixes =
    [| "AWS_"; "AZURE_"; "GCP_"; "DATABASE_URL" |]

/// Names that are always kept.
let private allowedNames =
    [| "PATH"; "HOME"; "SHELL"; "USER"; "LOGNAME"; "TERM"; "LANG"; "LC_ALL";
       "TMPDIR"; "XDG_RUNTIME_DIR"; "DOTNET_ROOT"; "JAVA_HOME"; "CARGO_HOME";
       "GOPATH"; "GOROOT"; "NVM_DIR" |]

/// Prefixes that are kept.
let private allowedPrefixes =
    [| "DOTNET_"; "GO_"; "JAVA_"; "CARGO_" |]

let private isSafe (key: string) : bool =
    let up = key.ToUpperInvariant()
    // Always strip known secret patterns
    if secretPrefixes |> Array.exists (fun p -> up.StartsWith(p, StringComparison.Ordinal)) then false
    elif secretSuffixes |> Array.exists (fun s -> up.EndsWith(s, StringComparison.Ordinal)) then false
    // Keep explicit allow-list
    elif allowedNames |> Array.contains up then true
    elif allowedPrefixes |> Array.exists (fun p -> up.StartsWith(p, StringComparison.Ordinal)) then true
    else false

/// Resolve the user's login shell and appropriate flags.
/// Most POSIX shells accept `-lc`; fish and nushell only accept `-c`.
let private resolveShell () : string * string =
    let shell =
        Environment.GetEnvironmentVariable "SHELL"
        |> Option.ofObj
        |> Option.filter (fun s -> s.Length > 0)
        |> Option.defaultValue "/bin/sh"
    let name = IO.Path.GetFileName shell
    let flags =
        match name with
        | "fish" | "nu" | "nush" | "xonsh" -> "-c"
        | _                                  -> "-lc"
    shell, flags

[<Description("Run a shell command using the user's login shell and return combined stdout/stderr with exit code.")>]
let bash
    ([<Description("Working directory.")>] cwd: string)
    ([<Description("Command line to execute (shell syntax allowed).")>] command: string)
    ([<Description("Timeout in milliseconds (default 60000 = 60s).")>] timeoutMs: int option)
    ([<Description("Strip secret env vars before starting the process.")>] cleanEnv: bool option)
    ([<Description("CancellationToken to interrupt the process early.")>] ct: CancellationToken)
    : string =
    let timeout = timeoutMs |> Option.defaultValue 60_000
    let shell, flags = resolveShell ()

    let psi = ProcessStartInfo(shell, flags + " \"" + command.Replace("\"", "\\\"") + "\"")
    psi.WorkingDirectory <- cwd
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false

    if cleanEnv = Some true then
        let env = psi.Environment
        let toRemove =
            env.Keys
            |> Seq.filter (fun k -> not (isSafe k))
            |> Seq.toArray
        for k in toRemove do env.Remove k |> ignore

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
