module Fugue.Core.SystemPrompt

open System
open System.Runtime.InteropServices

// Find git root by running `git rev-parse --show-toplevel` in dir.
// Returns None on failure (not a git repo, or git not installed).
let private findGitRoot (dir: string) : string option =
    try
        let psi = System.Diagnostics.ProcessStartInfo("git", "rev-parse --show-toplevel")
        psi.WorkingDirectory <- dir
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        match System.Diagnostics.Process.Start psi with
        | null -> None
        | p ->
            use _ = p
            p.WaitForExit 500 |> ignore
            if p.ExitCode = 0 then
                let out = (p.StandardOutput.ReadToEnd()).Trim()
                if String.IsNullOrEmpty out then None else Some out
            else None
    with _ -> None

/// Walk from startDir up to git root (or just startDir if not in a git repo),
/// collect any FUGUE.md files found, plus ~/.fugue/FUGUE.md.
/// Returns None if no files found. Returns Some with concatenated content if any found.
/// Order: global (~/.fugue/FUGUE.md) first, then parent-to-child (root → cwd last).
let loadFugueContext (startDir: string) : string option =
    let ceiling = findGitRoot startDir |> Option.defaultValue startDir

    // Collect directories from startDir up to ceiling (inclusive).
    // Result is ceiling-first, startDir-last (parent before child).
    let rec collectDirs (dir: string) acc =
        let normalized = System.IO.Path.GetFullPath dir
        let normalizedCeiling = System.IO.Path.GetFullPath ceiling
        if normalized = normalizedCeiling then normalizedCeiling :: acc
        elif normalized.StartsWith normalizedCeiling then
            match System.IO.Path.GetDirectoryName normalized with
            | null -> normalized :: acc  // filesystem root; stop
            | parent -> collectDirs parent (normalized :: acc)
        else acc  // went above ceiling somehow

    let dirs = collectDirs startDir []

    let found =
        dirs
        |> List.choose (fun d ->
            let candidate = System.IO.Path.Combine(d, "FUGUE.md")
            if System.IO.File.Exists candidate then Some (candidate, System.IO.File.ReadAllText candidate)
            else None)

    // Also check ~/.fugue/FUGUE.md
    let home = Environment.GetEnvironmentVariable "HOME" |> Option.ofObj |> Option.defaultValue ""
    let globalCtx =
        if home <> "" then
            let globalPath = System.IO.Path.Combine(home, ".fugue", "FUGUE.md")
            if System.IO.File.Exists globalPath then Some (globalPath, System.IO.File.ReadAllText globalPath)
            else None
        else None

    let allCtx =
        match globalCtx with
        | Some g -> g :: found
        | None -> found

    if List.isEmpty allCtx then None
    else
        let sections =
            allCtx |> List.map (fun (path, content) ->
                let rel =
                    if home <> "" && path.StartsWith home then "~" + path.Substring(home.Length)
                    else path
                sprintf "<!-- FUGUE.md: %s -->\n%s" rel content)
        Some (String.concat "\n\n" sections)

let render (cwd: string) (toolNames: string list) (context: string option) : string =
    let os =
        if RuntimeInformation.IsOSPlatform(OSPlatform.OSX) then "macOS"
        elif RuntimeInformation.IsOSPlatform(OSPlatform.Linux) then "Linux"
        elif RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then "Windows"
        else "Unknown"

    let date = DateTime.UtcNow.ToString("yyyy-MM-dd")
    let tools = String.Join(", ", toolNames)

    let contextSection =
        match context with
        | None -> ""
        | Some ctx -> "\n\n# Project context (from FUGUE.md)\n\n" + ctx

    $"""You are Fugue, a lean CLI coding assistant. You help the user with software engineering tasks in their terminal.

Working directory: {cwd}
Operating system: {os}
Today: {date}
Available tools: {tools}

# Tone
- Be concise. Default to short answers; expand only when asked.
- Prefer doing the task over describing it. Use tools.
- Never claim you ran a tool you did not run.

# Tools
- Read/Write/Edit work on text files. Edit requires an exact-match `old_string` that occurs once unless `replace_all` is set.
- Bash runs commands via `/bin/zsh -lc`. Default timeout is 120 seconds.
- Glob returns paths sorted by mtime descending.
- Grep returns matching lines with file:line prefixes.

# Style
- When you finish a task, stop. Do not summarize what you did unless asked.
- When you call a tool, do not also narrate "I will call X" — the user sees the call.
""" + contextSection
