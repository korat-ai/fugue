module Fugue.Cli.PromptRegistry

open System
open System.IO
open System.Reflection

// ── Types ─────────────────────────────────────────────────────────────────────

type PromptTemplate = {
    Command    : string
    Args       : string list
    Description: string
    Body       : string
    SourcePath : string   // "embedded" or full path to override file
}

// ── Frontmatter parser ────────────────────────────────────────────────────────

/// Parse a markdown template with optional YAML-ish frontmatter.
/// Frontmatter is between the first two "---" delimiters.
/// Fields supported: command, args, description.
/// args format: [a, b]  or  []
let parseTemplate (text: string) : PromptTemplate =
    let lines = text.Replace("\r\n", "\n").Split('\n')
    let hasFm = lines.Length > 0 && lines.[0].Trim() = "---"
    if not hasFm then
        { Command = ""; Args = []; Description = ""; Body = text; SourcePath = "embedded" }
    else
        // Find closing --- (skip the opening one at index 0)
        let endIdx =
            let mutable found = -1
            for i in 1 .. lines.Length - 1 do
                if found < 0 && lines.[i].Trim() = "---" then found <- i
            found
        if endIdx <= 0 then
            { Command = ""; Args = []; Description = ""; Body = text; SourcePath = "embedded" }
        else
            let fmLines = lines.[1..endIdx-1]
            let bodyLines = lines.[endIdx+1..]
            // Trim a single leading blank line that follows ---
            let bodyLines =
                if bodyLines.Length > 0 && bodyLines.[0].Trim() = "" then bodyLines.[1..]
                else bodyLines
            let body = String.concat "\n" bodyLines
            let kvs =
                fmLines
                |> Array.choose (fun line ->
                    let ci = line.IndexOf(':')
                    if ci > 0 then
                        let k = line.[..ci-1].Trim()
                        let v = line.[ci+1..].Trim()
                        Some (k, v)
                    else None)
                |> Map.ofArray
            let parseArgs (raw: string) =
                let inner = raw.Trim().TrimStart('[').TrimEnd(']').Trim()
                if inner = "" then []
                else inner.Split(',') |> Array.map (fun s -> s.Trim()) |> Array.toList
            { Command     = Map.tryFind "command" kvs |> Option.defaultValue ""
              Args        = Map.tryFind "args" kvs |> Option.defaultValue "[]" |> parseArgs
              Description = Map.tryFind "description" kvs |> Option.defaultValue ""
              Body        = body
              SourcePath  = "embedded" }

// ── Embedded resource loader ──────────────────────────────────────────────────

let private loadEmbedded (name: string) : string option =
    let asm = Assembly.GetExecutingAssembly()
    // MSBuild embeds as <RootNamespace>.<folder>.<filename>
    // RootNamespace is "Fugue.Cli", folder is "prompts", so: Fugue.Cli.prompts.<name>.md
    let resourceName = $"Fugue.Cli.prompts.{name}.md"
    match asm.GetManifestResourceStream(resourceName) with
    | null -> None
    | stream ->
        use s = stream
        use reader = new StreamReader(s)
        Some (reader.ReadToEnd())

// ── User-override dir ─────────────────────────────────────────────────────────

let private overridesDir () =
    match Environment.GetEnvironmentVariable "FUGUE_PROMPTS_DIR" |> Option.ofObj with
    | Some d when d <> "" -> d
    | _ ->
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".fugue", "prompts")

let private loadOverride (name: string) : (string * string) option =
    let dir = overridesDir ()
    let path = Path.Combine(dir, $"{name}.md")
    if File.Exists path then
        try Some (File.ReadAllText path, path)
        with ex ->
            eprintfn "fugue: warning: could not read prompt override %s: %s" path ex.Message
            None
    else None

// ── Embedded prompt names ─────────────────────────────────────────────────────
// Discovered dynamically from the AOT manifest at first call — eliminates the
// dual-maintenance burden of a hardcoded list. Adding a new template is
// strictly "drop a .md file under prompts/" + rebuild.

let private discoverEmbeddedNames () : string[] =
    let prefix = "Fugue.Cli.prompts."
    let suffix = ".md"
    Assembly.GetExecutingAssembly().GetManifestResourceNames()
    |> Array.choose (fun n ->
        if n.StartsWith prefix && n.EndsWith suffix then
            Some (n.Substring(prefix.Length, n.Length - prefix.Length - suffix.Length))
        else None)
    |> Array.sort

// ── Cache ─────────────────────────────────────────────────────────────────────

let mutable private cache : PromptTemplate list option = None

/// Reset the template cache (test seam).
let clearCache () = cache <- None

let private buildCache () : PromptTemplate list =
    let dir = overridesDir ()
    let embeddedNames = discoverEmbeddedNames ()
    // 1. All embedded templates
    let embedded =
        embeddedNames
        |> Array.choose (fun name ->
            match loadEmbedded name with
            | None ->
                eprintfn "fugue: warning: embedded prompt not found: %s" name
                None
            | Some text ->
                let tmpl = { parseTemplate text with SourcePath = "embedded" }
                Some tmpl)
        |> Array.toList
    // 2. User overrides — shadow embedded by Command name; also add user-only templates
    let overrides =
        try
            if Directory.Exists dir then
                Directory.GetFiles(dir, "*.md")
                |> Array.choose (fun path ->
                    try
                        let text = File.ReadAllText path
                        let tmpl = { parseTemplate text with SourcePath = path }
                        if tmpl.Command <> "" then Some tmpl
                        else
                            eprintfn "fugue: warning: prompt override at %s has no 'command' field" path
                            None
                    with ex ->
                        eprintfn "fugue: warning: could not load prompt override %s: %s" path ex.Message
                        None)
                |> Array.toList
            else []
        with _ -> []
    // Merge: user overrides take precedence by Command key
    let overrideMap = overrides |> List.map (fun t -> t.Command, t) |> Map.ofList
    let merged =
        embedded
        |> List.map (fun t ->
            match Map.tryFind t.Command overrideMap with
            | Some ov -> ov
            | None    -> t)
    // Append user-only templates (those whose Command doesn't match any embedded)
    let embeddedCmds = embedded |> List.map (fun t -> t.Command) |> Set.ofList
    let userOnly = overrides |> List.filter (fun t -> not (Set.contains t.Command embeddedCmds))
    merged @ userOnly

// ── Public API ────────────────────────────────────────────────────────────────

/// Return all known templates (embedded + user overrides), lazily cached.
let all () : PromptTemplate list =
    match cache with
    | Some c -> c
    | None ->
        let c = buildCache ()
        cache <- Some c
        c

/// Find a template by its Command name (e.g. "scaffold-cqrs").
let find (command: string) : PromptTemplate option =
    all () |> List.tryFind (fun t -> t.Command = command)

/// Substitute {var} placeholders in the template body.
/// Unknown placeholders are left as-is (no exception).
let render (tmpl: PromptTemplate) (args: Map<string, string>) : string =
    args |> Map.fold (fun body key value -> body.Replace($"{{{key}}}", value)) tmpl.Body
