[<Xunit.Collection("Sequential")>]
module Fugue.Tests.PromptRegistryTests

open System
open System.IO
open Xunit
open FsUnit.Xunit
open Fugue.Cli.PromptRegistry

// ── Helpers ───────────────────────────────────────────────────────────────────

/// Set FUGUE_PROMPTS_DIR for the duration of a test; returns a disposable that restores it.
let private useTmpPromptsDir () : string * IDisposable =
    let tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(tmp) |> ignore
    let saved = Environment.GetEnvironmentVariable "FUGUE_PROMPTS_DIR"
    Environment.SetEnvironmentVariable("FUGUE_PROMPTS_DIR", tmp)
    clearCache ()
    let restore =
        { new IDisposable with
            member _.Dispose() =
                Environment.SetEnvironmentVariable("FUGUE_PROMPTS_DIR",
                    match saved with null -> "" | s -> s)
                clearCache ()
                try Directory.Delete(tmp, true) with _ -> () }
    tmp, restore

// ── Test 1: parseTemplate populates command/args/description ──────────────────

[<Fact>]
let ``parseTemplate extracts frontmatter fields correctly`` () =
    let text = """---
command: scaffold-cqrs
args: [name, extra]
description: Generate CQRS slice
---
Body here."""
    let tmpl = parseTemplate text
    tmpl.Command     |> should equal "scaffold-cqrs"
    tmpl.Args        |> should equal ["name"; "extra"]
    tmpl.Description |> should equal "Generate CQRS slice"
    tmpl.Body        |> should equal "Body here."

// ── Test 2: parseTemplate body skips frontmatter delimiters ───────────────────

[<Fact>]
let ``parseTemplate body does not include frontmatter lines`` () =
    let text = """---
command: foo
args: []
description: Test
---
Line one.
Line two."""
    let tmpl = parseTemplate text
    tmpl.Body |> should not' (haveSubstring "---")
    tmpl.Body |> should not' (haveSubstring "command:")
    tmpl.Body |> should haveSubstring "Line one."
    tmpl.Body |> should haveSubstring "Line two."

// ── Test 3: find "scaffold-cqrs" returns Some on embedded ────────────────────

[<Fact>]
let ``find returns Some for embedded scaffold-cqrs template`` () =
    let _tmp, cleanup = useTmpPromptsDir ()
    use _ = cleanup
    let result = find "scaffold-cqrs"
    result |> should not' (equal None)
    result.Value.Command     |> should equal "scaffold-cqrs"
    result.Value.Description |> should not' (equal "")
    result.Value.Body        |> should haveSubstring "CQRS"

// ── Test 4: user override at FUGUE_PROMPTS_DIR shadows embedded ───────────────

[<Fact>]
let ``user override in FUGUE_PROMPTS_DIR shadows embedded template`` () =
    let tmp, cleanup = useTmpPromptsDir ()
    use _ = cleanup
    let overrideText = """---
command: scaffold-cqrs
args: [cmdName]
description: My override description
---
My custom CQRS body for {cmdName}."""
    File.WriteAllText(Path.Combine(tmp, "scaffold-cqrs.md"), overrideText)
    clearCache ()
    let result = find "scaffold-cqrs"
    result |> should not' (equal None)
    result.Value.Description |> should equal "My override description"
    result.Value.Body        |> should haveSubstring "My custom CQRS body"
    result.Value.SourcePath  |> should not' (equal "embedded")

// ── Test 5: missing FUGUE_PROMPTS_DIR falls back to embedded ──────────────────

[<Fact>]
let ``all falls back to embedded templates when FUGUE_PROMPTS_DIR does not exist`` () =
    let tmp, cleanup = useTmpPromptsDir ()
    use _ = cleanup
    // Point to a non-existent subdirectory so there are no override files
    Environment.SetEnvironmentVariable("FUGUE_PROMPTS_DIR", Path.Combine(tmp, "nonexistent"))
    clearCache ()
    let templates = all ()
    templates |> should not' (equal [])
    templates |> List.exists (fun t -> t.Command = "scaffold-cqrs") |> should be True
    templates |> List.exists (fun t -> t.SourcePath = "embedded")   |> should be True

// ── Test 6: render substitutes known vars, leaves unknown literal ─────────────

[<Fact>]
let ``render substitutes known vars and leaves unknown vars as-is`` () =
    let tmpl = { Command = "test"; Args = ["name"]; Description = "test"
                 Body = "Hello {name}, your type is {typeName}. Unknown: {missing}."
                 SourcePath = "embedded" }
    let args = Map.ofList ["name", "Alice"; "typeName", "MyType"]
    let result = render tmpl args
    result |> should equal "Hello Alice, your type is MyType. Unknown: {missing}."
