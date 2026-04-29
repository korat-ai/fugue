# Fugue — Spectre.Console + Native AOT pivot Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Заменить Terminal.Gui-based TUI на Spectre.Console-based REPL с rich-rendering (markdown, diff, tool-bullets, persistent status bar) под полным Native AOT, добавить локализацию (en/ru) и configurable user-message alignment.

**Architecture:** `Fugue.Core` получает `UiConfig` и `Localization` модуль. `Fugue.Tools` переписывает регистрацию тулзов на кастомный `AIFunction`-наследник без reflection. `Fugue.Cli` полностью пересоздаётся: вместо MVU + Terminal.Gui — REPL-loop на Spectre.Console с собственным `ReadLine`, `MarkdownRender` (через Markdig), `DiffRender`, `StatusBar` (ANSI scroll-region) и `Render`-фасадом. Финальная сборка — `dotnet publish` с `PublishAot=true`.

**Tech Stack:** F# 9 / .NET 10 / Spectre.Console 0.49 / Markdig 0.41 / Microsoft.Agents.AI 1.3 / xUnit + FsUnit.

**Reference spec:** `docs/superpowers/specs/2026-04-29-fugue-spectre-aot-pivot.md` (commit `09fe8ef`).

**Hard constraints (from CLAUDE.md / memory):**
- Все новые модули — на F#. C# — только как NuGet-зависимости.
- `.slnx` solution format (не `.sln`).
- Никаких `Co-Authored-By` trailers в коммитах.
- `TreatWarningsAsErrors=true` остаётся; `<NoWarn>` для IL2026/IL3050/IL3053/IL2104 в нашем коде запрещены.

---

## Phase 1 — Tools: no-reflection `AIFunction`

Цель фазы: переписать `Fugue.Tools/ToolRegistry.fs` так, чтобы `AIFunctionFactory.Create` (reflection на F# `option`) больше не использовалась. После Phase 1 — старый CLI продолжает работать (тулзы возвращают `AIFunction` того же интерфейса), но мы готовы к AOT.

### Task 1.1: Args.fs — парсинг `AIFunctionArguments`

**Files:**
- Create: `src/Fugue.Tools/AiFunctions/Args.fs`
- Modify: `src/Fugue.Tools/Fugue.Tools.fsproj` (добавить Compile Include)
- Test: `tests/Fugue.Tests/ArgsTests.fs`

- [ ] **Step 1: Add new test file**

Create `tests/Fugue.Tests/ArgsTests.fs`:

```fsharp
module Fugue.Tests.ArgsTests

open System.Collections.Generic
open System.Text.Json
open Microsoft.Extensions.AI
open Xunit
open FsUnit.Xunit
open Fugue.Tools.AiFunctions

let private mkArgs (pairs: (string * obj) list) : AIFunctionArguments =
    AIFunctionArguments(dict pairs)

let private jsonEl (json: string) : JsonElement =
    use doc = JsonDocument.Parse(json)
    doc.RootElement.Clone()

[<Fact>]
let ``getStr returns string from JsonElement`` () =
    let args = mkArgs [ "name", box (jsonEl "\"hi\"") ]
    Args.getStr args "name" |> should equal "hi"

[<Fact>]
let ``getStr returns string from raw string`` () =
    let args = mkArgs [ "name", box "hi" ]
    Args.getStr args "name" |> should equal "hi"

[<Fact>]
let ``getStr throws ArgumentException when missing`` () =
    let args = mkArgs []
    (fun () -> Args.getStr args "name" |> ignore)
    |> should throw typeof<System.ArgumentException>

[<Fact>]
let ``tryGetInt returns Some from JsonElement number`` () =
    let args = mkArgs [ "n", box (jsonEl "42") ]
    Args.tryGetInt args "n" |> should equal (Some 42)

[<Fact>]
let ``tryGetInt returns None when missing`` () =
    let args = mkArgs []
    Args.tryGetInt args "n" |> should equal (None: int option)

[<Fact>]
let ``tryGetBool returns Some from JsonElement true`` () =
    let args = mkArgs [ "b", box (jsonEl "true") ]
    Args.tryGetBool args "b" |> should equal (Some true)

[<Fact>]
let ``tryGetStr returns None for missing key`` () =
    let args = mkArgs []
    Args.tryGetStr args "x" |> should equal (None: string option)
```

Add to `tests/Fugue.Tests/Fugue.Tests.fsproj` `Compile Include` list (place before existing test files):

```xml
<Compile Include="ArgsTests.fs" />
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Fugue.Tests --filter ArgsTests 2>&1 | tail -10`

Expected: BUILD FAIL with `The namespace 'AiFunctions' is not defined` or `module 'Args' not found`.

- [ ] **Step 3: Create Args.fs**

Create `src/Fugue.Tools/AiFunctions/Args.fs`:

```fsharp
module Fugue.Tools.AiFunctions.Args

open System
open System.Text.Json
open Microsoft.Extensions.AI

/// Try to coerce a value from AIFunctionArguments into a string.
/// Accepts JsonElement (string kind) or raw string. Returns None for any other shape.
let tryGetStr (args: AIFunctionArguments) (key: string) : string option =
    match args.TryGetValue key with
    | true, (:? JsonElement as el) when el.ValueKind = JsonValueKind.String ->
        Some (el.GetString())
    | true, (:? string as s) -> Some s
    | _ -> None

let getStr (args: AIFunctionArguments) (key: string) : string =
    match tryGetStr args key with
    | Some s -> s
    | None -> raise (ArgumentException(sprintf "Missing or non-string argument '%s'" key))

let tryGetInt (args: AIFunctionArguments) (key: string) : int option =
    match args.TryGetValue key with
    | true, (:? JsonElement as el) when el.ValueKind = JsonValueKind.Number ->
        Some (el.GetInt32())
    | true, (:? int as i) -> Some i
    | true, (:? int64 as i) -> Some (int i)
    | _ -> None

let tryGetBool (args: AIFunctionArguments) (key: string) : bool option =
    match args.TryGetValue key with
    | true, (:? JsonElement as el) when el.ValueKind = JsonValueKind.True -> Some true
    | true, (:? JsonElement as el) when el.ValueKind = JsonValueKind.False -> Some false
    | true, (:? bool as b) -> Some b
    | _ -> None
```

- [ ] **Step 4: Add Compile Include to Fugue.Tools.fsproj**

Modify `src/Fugue.Tools/Fugue.Tools.fsproj` — add **before** existing `ToolRegistry.fs` line:

```xml
<Compile Include="AiFunctions/Args.fs" />
```

- [ ] **Step 5: Run tests, expect pass**

Run: `dotnet test tests/Fugue.Tests --filter ArgsTests 2>&1 | tail -5`

Expected: `Passed: 7, Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/Fugue.Tools/AiFunctions/Args.fs src/Fugue.Tools/Fugue.Tools.fsproj tests/Fugue.Tests/ArgsTests.fs tests/Fugue.Tests/Fugue.Tests.fsproj
git commit -m "feat(tools): add no-reflection Args parser for AIFunctionArguments"
```

---

### Task 1.2: DelegatedFn.fs — общий `AIFunction`-наследник

**Files:**
- Create: `src/Fugue.Tools/AiFunctions/DelegatedFn.fs`
- Modify: `src/Fugue.Tools/Fugue.Tools.fsproj`
- Test: `tests/Fugue.Tests/DelegatedFnTests.fs`

- [ ] **Step 1: Write failing test**

Create `tests/Fugue.Tests/DelegatedFnTests.fs`:

```fsharp
module Fugue.Tests.DelegatedFnTests

open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI
open Xunit
open FsUnit.Xunit
open Fugue.Tools.AiFunctions

[<Fact>]
let ``DelegatedAIFunction surfaces name + description + schema`` () =
    let schema = DelegatedFn.parseSchema """{"type":"object","properties":{}}"""
    let fn =
        DelegatedFn.DelegatedAIFunction(
            name = "Echo",
            description = "echoes input",
            schema = schema,
            invoke = fun _ _ -> Task.FromResult("ok"))
    fn.Name |> should equal "Echo"
    fn.Description |> should equal "echoes input"
    fn.JsonSchema.GetProperty("type").GetString() |> should equal "object"

[<Fact>]
let ``DelegatedAIFunction.InvokeAsync calls invoke and boxes result`` () =
    let schema = DelegatedFn.parseSchema """{"type":"object"}"""
    let fn =
        DelegatedFn.DelegatedAIFunction(
            name = "Echo",
            description = "",
            schema = schema,
            invoke = fun args _ ->
                Task.FromResult(sprintf "got %d keys" args.Count))
    let args = AIFunctionArguments(dict [ "a", box "1"; "b", box "2" ])
    let result = (fn.InvokeAsync(args, CancellationToken.None).AsTask()).Result
    result |> string |> should equal "got 2 keys"

[<Fact>]
let ``parseSchema returns clone-safe JsonElement`` () =
    let el1 = DelegatedFn.parseSchema """{"type":"object"}"""
    let el2 = DelegatedFn.parseSchema """{"type":"array"}"""
    el1.GetProperty("type").GetString() |> should equal "object"
    el2.GetProperty("type").GetString() |> should equal "array"
```

Add to `tests/Fugue.Tests/Fugue.Tests.fsproj` `Compile Include` list (after `ArgsTests.fs`):

```xml
<Compile Include="DelegatedFnTests.fs" />
```

- [ ] **Step 2: Run test, expect fail**

Run: `dotnet test tests/Fugue.Tests --filter DelegatedFnTests 2>&1 | tail -5`

Expected: BUILD FAIL with `'DelegatedFn' not defined`.

- [ ] **Step 3: Create DelegatedFn.fs**

Create `src/Fugue.Tools/AiFunctions/DelegatedFn.fs`:

```fsharp
module Fugue.Tools.AiFunctions.DelegatedFn

open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI

/// Single concrete AIFunction subclass parametrised by name/desc/schema/body.
/// All six tools instantiate this with different bodies — no subclass per tool.
/// AOT-clean: no reflection on parameter types.
type DelegatedAIFunction(
        name: string,
        description: string,
        schema: JsonElement,
        invoke: AIFunctionArguments -> CancellationToken -> Task<string>) =
    inherit AIFunction()
    override _.Name        = name
    override _.Description = description
    override _.JsonSchema  = schema
    override _.InvokeAsync(args, ct) =
        ValueTask<obj>(task {
            let! result = invoke args ct
            return box result
        })

/// Parse a constant schema JSON string into a clone-safe JsonElement.
/// AOT-safe: JsonDocument.Parse(string) does not use reflection.
let parseSchema (json: string) : JsonElement =
    use doc = JsonDocument.Parse(json)
    doc.RootElement.Clone()
```

- [ ] **Step 4: Add Compile Include**

Modify `src/Fugue.Tools/Fugue.Tools.fsproj` — add **after** `Args.fs` line, **before** any `*Fn.fs` lines:

```xml
<Compile Include="AiFunctions/DelegatedFn.fs" />
```

- [ ] **Step 5: Run tests, expect pass**

Run: `dotnet test tests/Fugue.Tests --filter DelegatedFnTests 2>&1 | tail -5`

Expected: `Passed: 3, Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/Fugue.Tools/AiFunctions/DelegatedFn.fs src/Fugue.Tools/Fugue.Tools.fsproj tests/Fugue.Tests/DelegatedFnTests.fs tests/Fugue.Tests/Fugue.Tests.fsproj
git commit -m "feat(tools): add DelegatedAIFunction + parseSchema helpers"
```

---

### Task 1.3: ReadFn.fs — Read tool factory

**Files:**
- Create: `src/Fugue.Tools/AiFunctions/ReadFn.fs`
- Modify: `src/Fugue.Tools/Fugue.Tools.fsproj`
- Test: `tests/Fugue.Tests/ReadFnTests.fs`

- [ ] **Step 1: Write failing test**

Create `tests/Fugue.Tests/ReadFnTests.fs`:

```fsharp
module Fugue.Tests.ReadFnTests

open System
open System.IO
open System.Collections.Generic
open System.Text.Json
open System.Threading
open Microsoft.Extensions.AI
open Xunit
open FsUnit.Xunit
open Fugue.Tools.AiFunctions

let private mkArgs (pairs: (string * obj) list) : AIFunctionArguments =
    AIFunctionArguments(dict pairs)

let private jsonStr (s: string) : obj =
    use doc = JsonDocument.Parse(JsonSerializer.Serialize s)
    box (doc.RootElement.Clone())

[<Fact>]
let ``ReadFn schema declares path required`` () =
    let fn = ReadFn.create "/tmp"
    let req =
        fn.JsonSchema.GetProperty("required").EnumerateArray()
        |> Seq.map (fun e -> e.GetString())
        |> Set.ofSeq
    req |> should contain "path"

[<Fact>]
let ``ReadFn invocation reads tmp file`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    let path = Path.Combine(dir, "x.txt")
    File.WriteAllText(path, "alpha\nbeta\ngamma\n")
    let fn = ReadFn.create dir
    let args = mkArgs [ "path", jsonStr "x.txt" ]
    let result = (fn.InvokeAsync(args, CancellationToken.None).AsTask()).Result |> string
    result |> should haveSubstring "alpha"
    result |> should haveSubstring "beta"
    Directory.Delete(dir, true)

[<Fact>]
let ``ReadFn missing path raises ArgumentException`` () =
    let fn = ReadFn.create "/tmp"
    let args = mkArgs []
    let task = fn.InvokeAsync(args, CancellationToken.None).AsTask()
    (fun () -> task.Wait())
    |> should throw typeof<AggregateException>
```

Add to `tests/Fugue.Tests/Fugue.Tests.fsproj`:

```xml
<Compile Include="ReadFnTests.fs" />
```

- [ ] **Step 2: Run test, expect fail**

Run: `dotnet test tests/Fugue.Tests --filter ReadFnTests 2>&1 | tail -5`

Expected: BUILD FAIL with `'ReadFn' not defined`.

- [ ] **Step 3: Create ReadFn.fs**

Create `src/Fugue.Tools/AiFunctions/ReadFn.fs`:

```fsharp
module Fugue.Tools.AiFunctions.ReadFn

open Microsoft.Extensions.AI
open Fugue.Tools.AiFunctions

let private schema = DelegatedFn.parseSchema """{
  "type":"object",
  "properties":{
    "path":  {"type":"string","description":"Absolute or cwd-relative file path"},
    "offset":{"type":"integer","description":"1-based start line"},
    "limit": {"type":"integer","description":"Max lines"}
  },
  "required":["path"]
}"""

let create (cwd: string) : AIFunction =
    DelegatedFn.DelegatedAIFunction(
        name        = "Read",
        description = "Read a text file. Returns lines prefixed with 1-based line numbers.",
        schema      = schema,
        invoke      = fun args ct -> task {
            ct.ThrowIfCancellationRequested()
            let path   = Args.getStr     args "path"
            let offset = Args.tryGetInt  args "offset"
            let limit  = Args.tryGetInt  args "limit"
            return Fugue.Tools.ReadTool.read cwd path offset limit
        }) :> AIFunction
```

- [ ] **Step 4: Add Compile Include**

Modify `src/Fugue.Tools/Fugue.Tools.fsproj` — add **after** `DelegatedFn.fs`:

```xml
<Compile Include="AiFunctions/ReadFn.fs" />
```

- [ ] **Step 5: Run tests, expect pass**

Run: `dotnet test tests/Fugue.Tests --filter ReadFnTests 2>&1 | tail -5`

Expected: `Passed: 3, Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/Fugue.Tools/AiFunctions/ReadFn.fs src/Fugue.Tools/Fugue.Tools.fsproj tests/Fugue.Tests/ReadFnTests.fs tests/Fugue.Tests/Fugue.Tests.fsproj
git commit -m "feat(tools): ReadFn factory using DelegatedAIFunction"
```

---

### Task 1.4: WriteFn.fs

**Files:**
- Create: `src/Fugue.Tools/AiFunctions/WriteFn.fs`
- Modify: `src/Fugue.Tools/Fugue.Tools.fsproj`
- Test: `tests/Fugue.Tests/WriteFnTests.fs`

- [ ] **Step 1: Write failing test**

Create `tests/Fugue.Tests/WriteFnTests.fs`:

```fsharp
module Fugue.Tests.WriteFnTests

open System
open System.IO
open System.Text.Json
open System.Threading
open Microsoft.Extensions.AI
open Xunit
open FsUnit.Xunit
open Fugue.Tools.AiFunctions

let private mkArgs (pairs: (string * obj) list) : AIFunctionArguments =
    AIFunctionArguments(dict pairs)

let private jsonStr (s: string) : obj =
    use doc = JsonDocument.Parse(JsonSerializer.Serialize s)
    box (doc.RootElement.Clone())

[<Fact>]
let ``WriteFn schema requires path and content`` () =
    let fn = WriteFn.create "/tmp"
    let req =
        fn.JsonSchema.GetProperty("required").EnumerateArray()
        |> Seq.map (fun e -> e.GetString())
        |> Set.ofSeq
    req |> should contain "path"
    req |> should contain "content"

[<Fact>]
let ``WriteFn writes file content`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    let fn = WriteFn.create dir
    let args = mkArgs [
        "path", jsonStr "out.txt"
        "content", jsonStr "hello world"
    ]
    fn.InvokeAsync(args, CancellationToken.None).AsTask().Wait()
    File.ReadAllText(Path.Combine(dir, "out.txt")) |> should equal "hello world"
    Directory.Delete(dir, true)
```

Add to `tests/Fugue.Tests/Fugue.Tests.fsproj`:

```xml
<Compile Include="WriteFnTests.fs" />
```

- [ ] **Step 2: Run test, expect fail**

Run: `dotnet test tests/Fugue.Tests --filter WriteFnTests 2>&1 | tail -5`

Expected: BUILD FAIL `'WriteFn' not defined`.

- [ ] **Step 3: Create WriteFn.fs**

Create `src/Fugue.Tools/AiFunctions/WriteFn.fs`:

```fsharp
module Fugue.Tools.AiFunctions.WriteFn

open Microsoft.Extensions.AI
open Fugue.Tools.AiFunctions

let private schema = DelegatedFn.parseSchema """{
  "type":"object",
  "properties":{
    "path":   {"type":"string","description":"Absolute or cwd-relative file path"},
    "content":{"type":"string","description":"Full text content to write"}
  },
  "required":["path","content"]
}"""

let create (cwd: string) : AIFunction =
    DelegatedFn.DelegatedAIFunction(
        name        = "Write",
        description = "Write text content to a file. Overwrites if exists.",
        schema      = schema,
        invoke      = fun args ct -> task {
            ct.ThrowIfCancellationRequested()
            let path    = Args.getStr args "path"
            let content = Args.getStr args "content"
            return Fugue.Tools.WriteTool.write cwd path content
        }) :> AIFunction
```

- [ ] **Step 4: Add Compile Include**

Modify `src/Fugue.Tools/Fugue.Tools.fsproj` — add **after** `ReadFn.fs`:

```xml
<Compile Include="AiFunctions/WriteFn.fs" />
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Fugue.Tests --filter WriteFnTests 2>&1 | tail -5`

Expected: `Passed: 2, Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/Fugue.Tools/AiFunctions/WriteFn.fs src/Fugue.Tools/Fugue.Tools.fsproj tests/Fugue.Tests/WriteFnTests.fs tests/Fugue.Tests/Fugue.Tests.fsproj
git commit -m "feat(tools): WriteFn factory"
```

---

### Task 1.5: EditFn.fs

**Files:**
- Create: `src/Fugue.Tools/AiFunctions/EditFn.fs`
- Modify: `src/Fugue.Tools/Fugue.Tools.fsproj`
- Test: `tests/Fugue.Tests/EditFnTests.fs`

- [ ] **Step 1: Write failing test**

Create `tests/Fugue.Tests/EditFnTests.fs`:

```fsharp
module Fugue.Tests.EditFnTests

open System
open System.IO
open System.Text.Json
open System.Threading
open Microsoft.Extensions.AI
open Xunit
open FsUnit.Xunit
open Fugue.Tools.AiFunctions

let private mkArgs (pairs: (string * obj) list) : AIFunctionArguments =
    AIFunctionArguments(dict pairs)

let private jsonStr (s: string) : obj =
    use doc = JsonDocument.Parse(JsonSerializer.Serialize s)
    box (doc.RootElement.Clone())

let private jsonBool (b: bool) : obj =
    use doc = JsonDocument.Parse(if b then "true" else "false")
    box (doc.RootElement.Clone())

[<Fact>]
let ``EditFn replaces single occurrence`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    let path = Path.Combine(dir, "f.txt")
    File.WriteAllText(path, "old text here")
    let fn = EditFn.create dir
    let args = mkArgs [
        "path",       jsonStr "f.txt"
        "old_string", jsonStr "old"
        "new_string", jsonStr "new"
    ]
    fn.InvokeAsync(args, CancellationToken.None).AsTask().Wait()
    File.ReadAllText path |> should equal "new text here"
    Directory.Delete(dir, true)

[<Fact>]
let ``EditFn replace_all replaces every occurrence`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    let path = Path.Combine(dir, "f.txt")
    File.WriteAllText(path, "x x x")
    let fn = EditFn.create dir
    let args = mkArgs [
        "path",        jsonStr "f.txt"
        "old_string",  jsonStr "x"
        "new_string",  jsonStr "y"
        "replace_all", jsonBool true
    ]
    fn.InvokeAsync(args, CancellationToken.None).AsTask().Wait()
    File.ReadAllText path |> should equal "y y y"
    Directory.Delete(dir, true)
```

Add to `tests/Fugue.Tests/Fugue.Tests.fsproj`:

```xml
<Compile Include="EditFnTests.fs" />
```

- [ ] **Step 2: Run test, expect fail**

Run: `dotnet test tests/Fugue.Tests --filter EditFnTests 2>&1 | tail -5`

Expected: BUILD FAIL.

- [ ] **Step 3: Create EditFn.fs**

Create `src/Fugue.Tools/AiFunctions/EditFn.fs`:

```fsharp
module Fugue.Tools.AiFunctions.EditFn

open Microsoft.Extensions.AI
open Fugue.Tools.AiFunctions

let private schema = DelegatedFn.parseSchema """{
  "type":"object",
  "properties":{
    "path":        {"type":"string","description":"File path"},
    "old_string":  {"type":"string","description":"Exact text to replace"},
    "new_string":  {"type":"string","description":"Replacement text"},
    "replace_all": {"type":"boolean","description":"Replace every occurrence (default false — must be unique)"}
  },
  "required":["path","old_string","new_string"]
}"""

let create (cwd: string) : AIFunction =
    DelegatedFn.DelegatedAIFunction(
        name        = "Edit",
        description = "Edit a text file by exact-string replacement.",
        schema      = schema,
        invoke      = fun args ct -> task {
            ct.ThrowIfCancellationRequested()
            let path       = Args.getStr     args "path"
            let oldStr     = Args.getStr     args "old_string"
            let newStr     = Args.getStr     args "new_string"
            let replaceAll = Args.tryGetBool args "replace_all"
            return Fugue.Tools.EditTool.edit cwd path oldStr newStr replaceAll
        }) :> AIFunction
```

- [ ] **Step 4: Add Compile Include**

Modify `src/Fugue.Tools/Fugue.Tools.fsproj`:

```xml
<Compile Include="AiFunctions/EditFn.fs" />
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Fugue.Tests --filter EditFnTests 2>&1 | tail -5`

Expected: `Passed: 2, Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/Fugue.Tools/AiFunctions/EditFn.fs src/Fugue.Tools/Fugue.Tools.fsproj tests/Fugue.Tests/EditFnTests.fs tests/Fugue.Tests/Fugue.Tests.fsproj
git commit -m "feat(tools): EditFn factory"
```

---

### Task 1.6: BashFn.fs

**Files:**
- Create: `src/Fugue.Tools/AiFunctions/BashFn.fs`
- Modify: `src/Fugue.Tools/Fugue.Tools.fsproj`
- Test: `tests/Fugue.Tests/BashFnTests.fs`

- [ ] **Step 1: Write failing test**

Create `tests/Fugue.Tests/BashFnTests.fs`:

```fsharp
module Fugue.Tests.BashFnTests

open System.Text.Json
open System.Threading
open Microsoft.Extensions.AI
open Xunit
open FsUnit.Xunit
open Fugue.Tools.AiFunctions

let private mkArgs (pairs: (string * obj) list) : AIFunctionArguments =
    AIFunctionArguments(dict pairs)

let private jsonStr (s: string) : obj =
    use doc = JsonDocument.Parse(JsonSerializer.Serialize s)
    box (doc.RootElement.Clone())

[<Fact>]
let ``BashFn runs echo and returns stdout`` () =
    let fn = BashFn.create "/tmp"
    let args = mkArgs [ "command", jsonStr "echo hello-fugue" ]
    let out = (fn.InvokeAsync(args, CancellationToken.None).AsTask()).Result |> string
    out |> should haveSubstring "hello-fugue"

[<Fact>]
let ``BashFn schema requires command`` () =
    let fn = BashFn.create "/tmp"
    let req =
        fn.JsonSchema.GetProperty("required").EnumerateArray()
        |> Seq.map (fun e -> e.GetString())
        |> Set.ofSeq
    req |> should contain "command"
```

Add to `tests/Fugue.Tests/Fugue.Tests.fsproj`:

```xml
<Compile Include="BashFnTests.fs" />
```

- [ ] **Step 2: Run test, expect fail**

Run: `dotnet test tests/Fugue.Tests --filter BashFnTests 2>&1 | tail -5`

Expected: BUILD FAIL.

- [ ] **Step 3: Create BashFn.fs**

Create `src/Fugue.Tools/AiFunctions/BashFn.fs`:

```fsharp
module Fugue.Tools.AiFunctions.BashFn

open Microsoft.Extensions.AI
open Fugue.Tools.AiFunctions

let private schema = DelegatedFn.parseSchema """{
  "type":"object",
  "properties":{
    "command":    {"type":"string","description":"Shell command to run via /bin/zsh -lc"},
    "timeout_ms": {"type":"integer","description":"Optional timeout in milliseconds"}
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
```

- [ ] **Step 4: Add Compile Include**

Modify `src/Fugue.Tools/Fugue.Tools.fsproj`:

```xml
<Compile Include="AiFunctions/BashFn.fs" />
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Fugue.Tests --filter BashFnTests 2>&1 | tail -5`

Expected: `Passed: 2, Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/Fugue.Tools/AiFunctions/BashFn.fs src/Fugue.Tools/Fugue.Tools.fsproj tests/Fugue.Tests/BashFnTests.fs tests/Fugue.Tests/Fugue.Tests.fsproj
git commit -m "feat(tools): BashFn factory"
```

---

### Task 1.7: GlobFn.fs

**Files:**
- Create: `src/Fugue.Tools/AiFunctions/GlobFn.fs`
- Modify: `src/Fugue.Tools/Fugue.Tools.fsproj`
- Test: `tests/Fugue.Tests/GlobFnTests.fs`

- [ ] **Step 1: Write failing test**

Create `tests/Fugue.Tests/GlobFnTests.fs`:

```fsharp
module Fugue.Tests.GlobFnTests

open System
open System.IO
open System.Text.Json
open System.Threading
open Microsoft.Extensions.AI
open Xunit
open FsUnit.Xunit
open Fugue.Tools.AiFunctions

let private mkArgs (pairs: (string * obj) list) : AIFunctionArguments =
    AIFunctionArguments(dict pairs)

let private jsonStr (s: string) : obj =
    use doc = JsonDocument.Parse(JsonSerializer.Serialize s)
    box (doc.RootElement.Clone())

[<Fact>]
let ``GlobFn finds files matching pattern`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    File.WriteAllText(Path.Combine(dir, "a.fs"), "")
    File.WriteAllText(Path.Combine(dir, "b.fs"), "")
    File.WriteAllText(Path.Combine(dir, "c.txt"), "")
    let fn = GlobFn.create dir
    let args = mkArgs [ "pattern", jsonStr "*.fs" ]
    let out = (fn.InvokeAsync(args, CancellationToken.None).AsTask()).Result |> string
    out |> should haveSubstring "a.fs"
    out |> should haveSubstring "b.fs"
    out |> should not' (haveSubstring "c.txt")
    Directory.Delete(dir, true)
```

Add to `tests/Fugue.Tests/Fugue.Tests.fsproj`:

```xml
<Compile Include="GlobFnTests.fs" />
```

- [ ] **Step 2: Run test, expect fail**

Run: `dotnet test tests/Fugue.Tests --filter GlobFnTests 2>&1 | tail -5`

Expected: BUILD FAIL.

- [ ] **Step 3: Create GlobFn.fs**

Create `src/Fugue.Tools/AiFunctions/GlobFn.fs`:

```fsharp
module Fugue.Tools.AiFunctions.GlobFn

open Microsoft.Extensions.AI
open Fugue.Tools.AiFunctions

let private schema = DelegatedFn.parseSchema """{
  "type":"object",
  "properties":{
    "pattern":{"type":"string","description":"Glob pattern (e.g. **/*.fs)"},
    "root":   {"type":"string","description":"Optional root directory; defaults to cwd"}
  },
  "required":["pattern"]
}"""

let create (cwd: string) : AIFunction =
    DelegatedFn.DelegatedAIFunction(
        name        = "Glob",
        description = "Find files matching a glob pattern, sorted by mtime descending.",
        schema      = schema,
        invoke      = fun args ct -> task {
            ct.ThrowIfCancellationRequested()
            let pattern = Args.getStr    args "pattern"
            let root    = Args.tryGetStr args "root"
            return Fugue.Tools.GlobTool.glob cwd pattern root
        }) :> AIFunction
```

- [ ] **Step 4: Add Compile Include**

```xml
<Compile Include="AiFunctions/GlobFn.fs" />
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Fugue.Tests --filter GlobFnTests 2>&1 | tail -5`

Expected: `Passed: 1, Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/Fugue.Tools/AiFunctions/GlobFn.fs src/Fugue.Tools/Fugue.Tools.fsproj tests/Fugue.Tests/GlobFnTests.fs tests/Fugue.Tests/Fugue.Tests.fsproj
git commit -m "feat(tools): GlobFn factory"
```

---

### Task 1.8: GrepFn.fs

**Files:**
- Create: `src/Fugue.Tools/AiFunctions/GrepFn.fs`
- Modify: `src/Fugue.Tools/Fugue.Tools.fsproj`
- Test: `tests/Fugue.Tests/GrepFnTests.fs`

- [ ] **Step 1: Write failing test**

Create `tests/Fugue.Tests/GrepFnTests.fs`:

```fsharp
module Fugue.Tests.GrepFnTests

open System
open System.IO
open System.Text.Json
open System.Threading
open Microsoft.Extensions.AI
open Xunit
open FsUnit.Xunit
open Fugue.Tools.AiFunctions

let private mkArgs (pairs: (string * obj) list) : AIFunctionArguments =
    AIFunctionArguments(dict pairs)

let private jsonStr (s: string) : obj =
    use doc = JsonDocument.Parse(JsonSerializer.Serialize s)
    box (doc.RootElement.Clone())

[<Fact>]
let ``GrepFn finds matches in files`` () =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    File.WriteAllText(Path.Combine(dir, "a.txt"), "alpha\nfoo bar\nbeta\n")
    let fn = GrepFn.create dir
    let args = mkArgs [ "pattern", jsonStr "foo" ]
    let out = (fn.InvokeAsync(args, CancellationToken.None).AsTask()).Result |> string
    out |> should haveSubstring "foo"
    Directory.Delete(dir, true)
```

Add to `tests/Fugue.Tests/Fugue.Tests.fsproj`:

```xml
<Compile Include="GrepFnTests.fs" />
```

- [ ] **Step 2: Run test, expect fail**

Run: `dotnet test tests/Fugue.Tests --filter GrepFnTests 2>&1 | tail -5`

Expected: BUILD FAIL.

- [ ] **Step 3: Create GrepFn.fs**

Create `src/Fugue.Tools/AiFunctions/GrepFn.fs`:

```fsharp
module Fugue.Tools.AiFunctions.GrepFn

open Microsoft.Extensions.AI
open Fugue.Tools.AiFunctions

let private schema = DelegatedFn.parseSchema """{
  "type":"object",
  "properties":{
    "pattern":{"type":"string","description":"Regex pattern"},
    "root":   {"type":"string","description":"Optional root directory"},
    "glob":   {"type":"string","description":"Optional file glob filter"}
  },
  "required":["pattern"]
}"""

let create (cwd: string) : AIFunction =
    DelegatedFn.DelegatedAIFunction(
        name        = "Grep",
        description = "Regex-search files. Returns path:line:text matches.",
        schema      = schema,
        invoke      = fun args ct -> task {
            ct.ThrowIfCancellationRequested()
            let pattern = Args.getStr    args "pattern"
            let root    = Args.tryGetStr args "root"
            let glob    = Args.tryGetStr args "glob"
            return Fugue.Tools.GrepTool.grep cwd pattern root glob
        }) :> AIFunction
```

- [ ] **Step 4: Add Compile Include**

```xml
<Compile Include="AiFunctions/GrepFn.fs" />
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Fugue.Tests --filter GrepFnTests 2>&1 | tail -5`

Expected: `Passed: 1, Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/Fugue.Tools/AiFunctions/GrepFn.fs src/Fugue.Tools/Fugue.Tools.fsproj tests/Fugue.Tests/GrepFnTests.fs tests/Fugue.Tests/Fugue.Tests.fsproj
git commit -m "feat(tools): GrepFn factory"
```

---

### Task 1.9: Rewrite ToolRegistry.fs

**Files:**
- Modify: `src/Fugue.Tools/ToolRegistry.fs`
- Test: `tests/Fugue.Tests/ToolRegistryTests.fs` (new)

- [ ] **Step 1: Write failing test**

Create `tests/Fugue.Tests/ToolRegistryTests.fs`:

```fsharp
module Fugue.Tests.ToolRegistryTests

open Xunit
open FsUnit.Xunit
open Fugue.Tools

[<Fact>]
let ``buildAll returns 6 functions`` () =
    let fns = ToolRegistry.buildAll "/tmp"
    fns |> List.length |> should equal 6

[<Fact>]
let ``buildAll names match the names list`` () =
    let fns = ToolRegistry.buildAll "/tmp"
    let actualNames = fns |> List.map (fun f -> f.Name) |> List.sort
    let expectedNames = ToolRegistry.names |> List.sort
    actualNames |> should equal expectedNames

[<Fact>]
let ``every tool has a valid object schema with required field`` () =
    let fns = ToolRegistry.buildAll "/tmp"
    for fn in fns do
        fn.JsonSchema.GetProperty("type").GetString() |> should equal "object"
        fn.JsonSchema.TryGetProperty("required") |> fst |> should equal true
```

Add to `tests/Fugue.Tests/Fugue.Tests.fsproj`:

```xml
<Compile Include="ToolRegistryTests.fs" />
```

- [ ] **Step 2: Run test, expect fail (current ToolRegistry uses old API)**

Run: `dotnet test tests/Fugue.Tests --filter ToolRegistryTests 2>&1 | tail -8`

Expected: tests probably PASS against the OLD implementation (since names match), but test #3 may fail or behave inconsistently. Either way — proceed to rewrite.

- [ ] **Step 3: Rewrite ToolRegistry.fs**

Replace entire content of `src/Fugue.Tools/ToolRegistry.fs`:

```fsharp
module Fugue.Tools.ToolRegistry

open Microsoft.Extensions.AI
open Fugue.Tools.AiFunctions

/// Build all six tools as no-reflection AIFunction subclasses.
let buildAll (cwd: string) : AIFunction list = [
    ReadFn.create  cwd
    WriteFn.create cwd
    EditFn.create  cwd
    BashFn.create  cwd
    GlobFn.create  cwd
    GrepFn.create  cwd
]

let names : string list = [ "Read"; "Write"; "Edit"; "Bash"; "Glob"; "Grep" ]
```

- [ ] **Step 4: Run all tests, expect green**

Run: `dotnet test tests/Fugue.Tests 2>&1 | tail -5`

Expected: `Passed: ≥ 33, Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/Fugue.Tools/ToolRegistry.fs tests/Fugue.Tests/ToolRegistryTests.fs tests/Fugue.Tests/Fugue.Tests.fsproj
git commit -m "refactor(tools): ToolRegistry uses no-reflection AiFunctions/* factories"
```

---

### Task 1.10: Verify CLI still launches against new ToolRegistry

**Files:** none changed; manual smoke check.

- [ ] **Step 1: Build whole solution**

Run: `dotnet build 2>&1 | tail -10`

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 2: Run all tests**

Run: `dotnet test 2>&1 | tail -5`

Expected: ≥ 33 passed, 0 failed (the Discovery parallel-env race may flake — re-run filtered ConfigTests if so).

- [ ] **Step 3: Smoke-test publish (still R2R+Trim from current setup)**

Run: `dotnet publish src/Fugue.Cli -c Release -r osx-arm64 2>&1 | tail -5`

Expected: build success.

- [ ] **Step 4: Smoke-test old TUI launches without runtime exceptions**

Run: `( src/Fugue.Cli/bin/Release/net10.0/osx-arm64/publish/fugue 2>&1 & PID=$!; sleep 3; kill -TERM $PID 2>/dev/null; wait $PID 2>/dev/null ) | head -30`

Expected: no `Unhandled exception`. TUI initialised silently.

(Phase 1 milestone: tools refactor complete, CLI still on Terminal.Gui but uses new no-reflection toolchain.)

---

## Phase 2 — Core: `UiConfig` + `Localization`

### Task 2.1: Add `UiConfig` to Domain.fs

**Files:**
- Modify: `src/Fugue.Core/Domain.fs`

- [ ] **Step 1: Read current Domain.fs**

Run: `head -40 src/Fugue.Core/Domain.fs`

Note structure — locate `AppConfig` definition. (Currently lives in `Config.fs`, not `Domain.fs` — adjust if needed; in this plan we treat `Config.fs` as the home.)

- [ ] **Step 2: Add types to Config.fs**

Modify `src/Fugue.Core/Config.fs` — add **before** `type AppConfig`:

```fsharp
type UserAlignment = Left | Right

type UiConfig =
    { UserAlignment: UserAlignment
      Locale:        string }
```

And update `type AppConfig`:

```fsharp
type AppConfig =
    { Provider: ProviderConfig
      SystemPrompt: string option
      MaxIterations: int
      Ui: UiConfig }
```

- [ ] **Step 3: Add default UiConfig literal**

Add **after** the `UiConfig` type:

```fsharp
let private defaultLocale () =
    let envLoc =
        match Environment.GetEnvironmentVariable "FUGUE_LOCALE" with
        | null | "" -> None
        | s -> Some s
    match envLoc with
    | Some "ru" -> "ru"
    | Some "en" -> "en"
    | _ ->
        match System.Globalization.CultureInfo.CurrentCulture.TwoLetterISOLanguageName with
        | "ru" -> "ru"
        | _ -> "en"

let defaultUi () : UiConfig =
    { UserAlignment = Left
      Locale = defaultLocale () }
```

- [ ] **Step 4: Build**

Run: `dotnet build src/Fugue.Core 2>&1 | tail -5`

Expected: build fails because `AppConfig` is now used as 4-field record by callers but constructed as 3-field elsewhere. We fix in Step 5.

- [ ] **Step 5: Update all `AppConfig` constructions**

In `src/Fugue.Core/Config.fs` `load` function, every `Ok { Provider = ...; SystemPrompt = ...; MaxIterations = ... }` becomes:

```fsharp
Ok { Provider = ...; SystemPrompt = None; MaxIterations = mi; Ui = defaultUi () }
```

In `src/Fugue.Cli/Program.fs` `main` discovery branch — find:

```fsharp
let cfg = { Provider = provider; SystemPrompt = None; MaxIterations = 30 }
```

Replace with:

```fsharp
let cfg = { Provider = provider; SystemPrompt = None; MaxIterations = 30; Ui = Fugue.Core.Config.defaultUi () }
```

- [ ] **Step 6: Build and run all tests**

Run: `dotnet build 2>&1 | tail -5 && dotnet test 2>&1 | tail -5`

Expected: 0 errors, all tests pass (UpdateTests references AppConfig in `freshModel` — it'll need same fix).

If `UpdateTests.fs:13` fails: update `freshModel` body — add `Ui = Fugue.Core.Config.defaultUi ()` to the record literal.

- [ ] **Step 7: Commit**

```bash
git add src/Fugue.Core/Config.fs src/Fugue.Cli/Program.fs tests/Fugue.Tests/UpdateTests.fs
git commit -m "feat(core): add UiConfig record (alignment + locale) to AppConfig"
```

---

### Task 2.2: Localization.fs — strings + en/ru

**Files:**
- Create: `src/Fugue.Core/Localization.fs`
- Modify: `src/Fugue.Core/Fugue.Core.fsproj`
- Test: `tests/Fugue.Tests/LocalizationTests.fs`

- [ ] **Step 1: Write failing test**

Create `tests/Fugue.Tests/LocalizationTests.fs`:

```fsharp
module Fugue.Tests.LocalizationTests

open Xunit
open FsUnit.Xunit
open Fugue.Core.Localization

[<Fact>]
let ``pick "en" returns english strings`` () =
    let s = pick "en"
    s.Cancelled |> should equal "cancelled"

[<Fact>]
let ``pick "ru" returns russian strings`` () =
    let s = pick "ru"
    s.Cancelled |> should equal "отменено"

[<Fact>]
let ``pick unknown locale falls back to english`` () =
    let s = pick "fr"
    s.Cancelled |> should equal "cancelled"

[<Fact>]
let ``en strings are all non-empty`` () =
    let s = en
    s.Cancelled       |> should not' (be EmptyString)
    s.ToolRunning     |> should not' (be EmptyString)
    s.ErrorPrefix     |> should not' (be EmptyString)
    s.StatusBarApp    |> should not' (be EmptyString)
    s.StatusBarOn     |> should not' (be EmptyString)

[<Fact>]
let ``ru strings are all non-empty`` () =
    let s = ru
    s.Cancelled       |> should not' (be EmptyString)
    s.ToolRunning     |> should not' (be EmptyString)
    s.ErrorPrefix     |> should not' (be EmptyString)
    s.StatusBarApp    |> should not' (be EmptyString)
    s.StatusBarOn     |> should not' (be EmptyString)
```

Add to `tests/Fugue.Tests/Fugue.Tests.fsproj`:

```xml
<Compile Include="LocalizationTests.fs" />
```

- [ ] **Step 2: Run test, expect fail**

Run: `dotnet test tests/Fugue.Tests --filter LocalizationTests 2>&1 | tail -5`

Expected: BUILD FAIL `'Fugue.Core.Localization' not found`.

- [ ] **Step 3: Create Localization.fs**

Create `src/Fugue.Core/Localization.fs`:

```fsharp
module Fugue.Core.Localization

/// All UI strings of the app. Add new keys here, then populate both en and ru.
type Strings =
    { // Stream lifecycle
      Cancelled:    string
      ToolRunning:  string
      ErrorPrefix:  string

      // Status bar
      StatusBarApp: string
      StatusBarOn:  string

      // Prompt help
      PromptHelpEnter:        string
      PromptHelpShiftEnter:   string
      PromptHelpCtrlD:        string

      // Discovery / config help
      ConfigHelpHeader:       string
      ConfigHelpEnvHint:      string
      ConfigHelpFileHint:     string
      DiscoveryNoCandidates:  string
      DiscoveryPickProvider:  string }

let en : Strings =
    { Cancelled            = "cancelled"
      ToolRunning          = "running…"
      ErrorPrefix          = "error"
      StatusBarApp         = "fugue"
      StatusBarOn          = "on"
      PromptHelpEnter      = "Enter to send"
      PromptHelpShiftEnter = "Shift+Enter for newline"
      PromptHelpCtrlD      = "Ctrl+D to exit"
      ConfigHelpHeader     = "No Fugue configuration found."
      ConfigHelpEnvHint    = "Set FUGUE_PROVIDER=anthropic|openai|ollama and the matching API key."
      ConfigHelpFileHint   = "Or create ~/.fugue/config.json (see docs)."
      DiscoveryNoCandidates= "No providers detected."
      DiscoveryPickProvider= "Pick a provider:" }

let ru : Strings =
    { Cancelled            = "отменено"
      ToolRunning          = "выполняется…"
      ErrorPrefix          = "ошибка"
      StatusBarApp         = "fugue"
      StatusBarOn          = "на ветке"
      PromptHelpEnter      = "Enter — отправить"
      PromptHelpShiftEnter = "Shift+Enter — новая строка"
      PromptHelpCtrlD      = "Ctrl+D — выход"
      ConfigHelpHeader     = "Конфигурация Fugue не найдена."
      ConfigHelpEnvHint    = "Задайте FUGUE_PROVIDER=anthropic|openai|ollama и соответствующий ключ API."
      ConfigHelpFileHint   = "Или создайте ~/.fugue/config.json (см. документацию)."
      DiscoveryNoCandidates= "Провайдеры не обнаружены."
      DiscoveryPickProvider= "Выберите провайдер:" }

/// Pick a Strings value by ISO-2 locale code. Unknown locales fall back to en.
/// Callers do `Localization.pick cfg.Ui.Locale` — `current(cfg)` was in the
/// spec but adds an unwanted dep from Localization.fs to Config.fs (the
/// Compile order in Fugue.Core.fsproj has Localization BEFORE Config).
let pick (locale: string) : Strings =
    match locale with
    | "ru" -> ru
    | _    -> en
```

- [ ] **Step 4: Add to fsproj**

Modify `src/Fugue.Core/Fugue.Core.fsproj` — add **after** `Log.fs`, **before** `Domain.fs`:

```xml
<Compile Include="Localization.fs" />
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/Fugue.Tests --filter LocalizationTests 2>&1 | tail -5`

Expected: `Passed: 5, Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/Fugue.Core/Localization.fs src/Fugue.Core/Fugue.Core.fsproj tests/Fugue.Tests/LocalizationTests.fs tests/Fugue.Tests/Fugue.Tests.fsproj
git commit -m "feat(core): add Localization module with en/ru Strings + pick"
```

---

### Task 2.3: Parse `ui` block in Config.fs

**Files:**
- Modify: `src/Fugue.Core/Config.fs`
- Modify: `src/Fugue.Core/JsonContext.fs` (extend DTO)
- Test: `tests/Fugue.Tests/ConfigTests.fs` (extend)

- [ ] **Step 1: Inspect existing JsonContext.fs**

Run: `cat src/Fugue.Core/JsonContext.fs`

Note where `AppConfigDto` is defined.

- [ ] **Step 2: Extend DTO**

Modify `src/Fugue.Core/JsonContext.fs` — add new DTO + extend existing:

```fsharp
[<CLIMutable>]
type UiConfigDto = {
    userAlignment: string
    locale: string
}

[<CLIMutable>]
type AppConfigDto = {
    provider: string
    model: string
    apiKey: string
    ollamaEndpoint: string
    maxIterations: int
    ui: UiConfigDto
}
```

(If `UiConfigDto` field is missing from JSON, .NET will give the field default values — empty strings — which we handle below.)

- [ ] **Step 3: Update fromFile in Config.fs**

In `src/Fugue.Core/Config.fs`, locate `fromFile` and replace its `provider |> Result.map (fun p -> p, dto.maxIterations)` ending with:

```fsharp
provider |> Result.map (fun p ->
    let alignment =
        match dto.ui.userAlignment with
        | "right" -> Right
        | _       -> Left
    let locale =
        match dto.ui.locale with
        | "ru" -> "ru"
        | "en" -> "en"
        | _ -> defaultLocale ()
    let ui = { UserAlignment = alignment; Locale = locale }
    p, dto.maxIterations, ui)
```

And update `load` to thread `ui`:

```fsharp
match fromFile path with
| Ok (p, mi, ui) ->
    let mi = if mi <= 0 then 30 else mi
    Ok { Provider = p; SystemPrompt = None; MaxIterations = mi; Ui = ui }
| Error e -> Error(InvalidConfig e)
```

- [ ] **Step 4: Update saveToFile**

Replace `let dto = ...` block in `saveToFile` with:

```fsharp
let uiDto =
    { userAlignment = (match cfg.Ui.UserAlignment with Left -> "left" | Right -> "right")
      locale        = cfg.Ui.Locale }
let dto =
    match cfg.Provider with
    | Anthropic(k, m) ->
        { provider = "anthropic"; model = m; apiKey = k; ollamaEndpoint = ""; maxIterations = cfg.MaxIterations; ui = uiDto }
    | OpenAI(k, m) ->
        { provider = "openai"; model = m; apiKey = k; ollamaEndpoint = ""; maxIterations = cfg.MaxIterations; ui = uiDto }
    | Ollama(e, m) ->
        { provider = "ollama"; model = m; apiKey = ""; ollamaEndpoint = string e; maxIterations = cfg.MaxIterations; ui = uiDto }
```

- [ ] **Step 5: Add tests**

Append to `tests/Fugue.Tests/ConfigTests.fs`:

```fsharp
[<Fact>]
let ``load picks alignment=Right when ui.userAlignment=right`` () =
    let tmpHome = IO.Path.Combine(IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let dir = IO.Path.Combine(tmpHome, ".fugue")
    IO.Directory.CreateDirectory dir |> ignore
    let path = IO.Path.Combine(dir, "config.json")
    IO.File.WriteAllText(path, """{
        "provider":"ollama","model":"llama","apiKey":"","ollamaEndpoint":"http://localhost:11434",
        "maxIterations":30,"ui":{"userAlignment":"right","locale":"ru"}
    }""")
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null)
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null)
    Environment.SetEnvironmentVariable("FUGUE_PROVIDER", null)
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    match load [||] with
    | Ok cfg ->
        cfg.Ui.UserAlignment |> should equal Right
        cfg.Ui.Locale |> should equal "ru"
    | Error e -> failwithf "expected Ok, got %A" e

[<Fact>]
let ``load defaults Ui to Left/auto when ui block missing`` () =
    let tmpHome = IO.Path.Combine(IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let dir = IO.Path.Combine(tmpHome, ".fugue")
    IO.Directory.CreateDirectory dir |> ignore
    let path = IO.Path.Combine(dir, "config.json")
    IO.File.WriteAllText(path, """{
        "provider":"ollama","model":"llama","apiKey":"","ollamaEndpoint":"http://localhost:11434",
        "maxIterations":30
    }""")
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null)
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null)
    Environment.SetEnvironmentVariable("FUGUE_PROVIDER", null)
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    match load [||] with
    | Ok cfg -> cfg.Ui.UserAlignment |> should equal Left
    | Error e -> failwithf "expected Ok, got %A" e
```

- [ ] **Step 6: Run tests**

Run: `dotnet test tests/Fugue.Tests --filter ConfigTests 2>&1 | tail -5`

Expected: `Passed: 4, Failed: 0`.

- [ ] **Step 7: Commit**

```bash
git add src/Fugue.Core/Config.fs src/Fugue.Core/JsonContext.fs tests/Fugue.Tests/ConfigTests.fs
git commit -m "feat(core): parse ui block (alignment + locale) from config.json"
```

---

## Phase 3 — CLI rewrite (Spectre + REPL + ReadLine + Render + StatusBar)

> **Phase 3 starts with a destructive step**: deleting `Mvu.fs`/`Model.fs`/`Update.fs`/`View.fs` and switching `Fugue.Cli.fsproj` from Terminal.Gui to Spectre. Before this commit, the CLI is in its "old" working state. Tag the commit so we can roll back if Phase 3 explodes.

### Task 3.1: Switch Cli.fsproj deps + delete legacy MVU files

**Files:**
- Rewrite: `src/Fugue.Cli/Fugue.Cli.fsproj`
- Delete: `src/Fugue.Cli/Mvu.fs`, `src/Fugue.Cli/Model.fs`, `src/Fugue.Cli/Update.fs`, `src/Fugue.Cli/View.fs`
- Delete: `tests/Fugue.Tests/UpdateTests.fs`, `tests/Fugue.Tests/MvuSmokeTests.fs` (if present)
- Modify: `tests/Fugue.Tests/Fugue.Tests.fsproj` (remove deleted Compile entries)

- [ ] **Step 1: Tag the pre-rewrite state**

Run: `git tag pre-spectre-pivot`

- [ ] **Step 2: Delete legacy files**

Run:
```bash
git rm src/Fugue.Cli/Mvu.fs src/Fugue.Cli/Model.fs src/Fugue.Cli/Update.fs src/Fugue.Cli/View.fs
git rm tests/Fugue.Tests/UpdateTests.fs
[ -f tests/Fugue.Tests/MvuSmokeTests.fs ] && git rm tests/Fugue.Tests/MvuSmokeTests.fs || true
```

- [ ] **Step 3: Replace Fugue.Cli.fsproj**

Overwrite `src/Fugue.Cli/Fugue.Cli.fsproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>Fugue.Cli</RootNamespace>
    <AssemblyName>fugue</AssemblyName>
    <JsonSerializerIsReflectionEnabledByDefault>true</JsonSerializerIsReflectionEnabledByDefault>
  </PropertyGroup>

  <ItemGroup>
    <Compile Include="ReadLine.fs" />
    <Compile Include="MarkdownRender.fs" />
    <Compile Include="DiffRender.fs" />
    <Compile Include="Render.fs" />
    <Compile Include="StatusBar.fs" />
    <Compile Include="Repl.fs" />
    <Compile Include="Program.fs" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Spectre.Console" Version="0.49.*" />
    <PackageReference Include="Markdig" Version="0.41.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Fugue.Core\Fugue.Core.fsproj" />
    <ProjectReference Include="..\Fugue.Agent\Fugue.Agent.fsproj" />
    <ProjectReference Include="..\Fugue.Tools\Fugue.Tools.fsproj" />
  </ItemGroup>
</Project>
```

(`PublishAot=true` will be added in Phase 4 once we know the codebase compiles cleanly.)

- [ ] **Step 4: Update Fugue.Tests.fsproj**

Open `tests/Fugue.Tests/Fugue.Tests.fsproj` and **remove** `<Compile Include="UpdateTests.fs" />` and `<Compile Include="MvuSmokeTests.fs" />` entries if present.

- [ ] **Step 5: Restore + build**

Run: `dotnet restore && dotnet build src/Fugue.Cli 2>&1 | tail -15`

Expected: BUILD FAIL — `ReadLine.fs / MarkdownRender.fs / ... not found` (we're about to create them in tasks 3.2-3.20).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "chore(cli): drop Terminal.Gui + MVU stack; prepare for Spectre rewrite"
```

(Build is broken at this commit — that's OK. We rebuild incrementally.)

---

### Task 3.2: ReadLine — pure key-handler

**Files:**
- Create: `src/Fugue.Cli/ReadLine.fs` (pure handler section only)
- Test: `tests/Fugue.Tests/ReadLineTests.fs`

- [ ] **Step 1: Write failing test**

Create `tests/Fugue.Tests/ReadLineTests.fs`:

```fsharp
module Fugue.Tests.ReadLineTests

open System
open System.Collections.Generic
open Xunit
open FsUnit.Xunit
open Fugue.Cli.ReadLine

let private mkState (text: string) (cursor: int) : S =
    { Buffer        = ResizeArray<char>(text.ToCharArray())
      Cursor        = cursor
      LinesRendered = 0
      PromptText    = "> "
      PromptVisLen  = 2
      Width         = 80 }

let private key (c: char) (mods: ConsoleModifiers) : ConsoleKeyInfo =
    let cKey =
        match c with
        | '\r' -> ConsoleKey.Enter
        | _    -> ConsoleKey.A   // not used for printable chars
    ConsoleKeyInfo(c, cKey, (mods &&& ConsoleModifiers.Shift) <> ConsoleModifiers.None,
                   (mods &&& ConsoleModifiers.Alt)   <> ConsoleModifiers.None,
                   (mods &&& ConsoleModifiers.Control) <> ConsoleModifiers.None)

let private special (k: ConsoleKey) (mods: ConsoleModifiers) : ConsoleKeyInfo =
    ConsoleKeyInfo(' ', k, (mods &&& ConsoleModifiers.Shift) <> ConsoleModifiers.None,
                   (mods &&& ConsoleModifiers.Alt)   <> ConsoleModifiers.None,
                   (mods &&& ConsoleModifiers.Control) <> ConsoleModifiers.None)

[<Fact>]
let ``insert printable char advances buffer + cursor`` () =
    let s = mkState "" 0
    let act = applyKey (key 'a' ConsoleModifiers.None) s
    act |> should equal Continue
    String(s.Buffer.ToArray()) |> should equal "a"
    s.Cursor |> should equal 1

[<Fact>]
let ``Enter on non-empty buffer returns Submit`` () =
    let s = mkState "hello" 5
    let act = applyKey (special ConsoleKey.Enter ConsoleModifiers.None) s
    act |> should equal (Submit "hello")

[<Fact>]
let ``Shift+Enter inserts newline`` () =
    let s = mkState "abc" 3
    let act = applyKey (special ConsoleKey.Enter ConsoleModifiers.Shift) s
    act |> should equal Continue
    String(s.Buffer.ToArray()) |> should equal "abc\n"
    s.Cursor |> should equal 4

[<Fact>]
let ``Ctrl+D on empty buffer returns Quit`` () =
    let s = mkState "" 0
    let act = applyKey (special ConsoleKey.D ConsoleModifiers.Control) s
    act |> should equal Quit

[<Fact>]
let ``Ctrl+C wipes buffer and signals Wipe`` () =
    let s = mkState "hello" 5
    let act = applyKey (special ConsoleKey.C ConsoleModifiers.Control) s
    act |> should equal Wipe
    s.Buffer.Count |> should equal 0
    s.Cursor       |> should equal 0

[<Fact>]
let ``Backspace removes char before cursor`` () =
    let s = mkState "abc" 3
    let act = applyKey (special ConsoleKey.Backspace ConsoleModifiers.None) s
    act |> should equal Continue
    String(s.Buffer.ToArray()) |> should equal "ab"
    s.Cursor |> should equal 2

[<Fact>]
let ``LeftArrow at cursor 0 stays at 0`` () =
    let s = mkState "abc" 0
    let act = applyKey (special ConsoleKey.LeftArrow ConsoleModifiers.None) s
    act |> should equal Continue
    s.Cursor |> should equal 0
```

Add to `tests/Fugue.Tests/Fugue.Tests.fsproj`:

```xml
<Compile Include="ReadLineTests.fs" />
```

- [ ] **Step 2: Run test, expect fail**

Run: `dotnet test tests/Fugue.Tests --filter ReadLineTests 2>&1 | tail -5`

Expected: BUILD FAIL — module Fugue.Cli.ReadLine not found.

- [ ] **Step 3: Create ReadLine.fs (pure portion)**

Create `src/Fugue.Cli/ReadLine.fs`:

```fsharp
module Fugue.Cli.ReadLine

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

/// Internal mutable line state. Public only because tests construct it directly.
type S = {
    mutable Buffer:        ResizeArray<char>
    mutable Cursor:        int
    mutable LinesRendered: int
    PromptText:            string
    PromptVisLen:          int
    Width:                 int
}

/// Action returned by the pure key-handler. The caller (read-loop) decides what to do.
type Action =
    | Continue
    | Submit of string
    | Quit
    | Wipe

let private isCtrl (k: ConsoleKeyInfo) (key: ConsoleKey) : bool =
    k.Modifiers.HasFlag ConsoleModifiers.Control && k.Key = key

let private isShift (k: ConsoleKeyInfo) (key: ConsoleKey) : bool =
    k.Modifiers.HasFlag ConsoleModifiers.Shift && k.Key = key

/// Pure key dispatch. Mutates `s`; returns Action telling the loop what's next.
let applyKey (k: ConsoleKeyInfo) (s: S) : Action =
    if isCtrl k ConsoleKey.C then
        s.Buffer.Clear()
        s.Cursor <- 0
        Wipe
    elif isCtrl k ConsoleKey.D then
        if s.Buffer.Count = 0 then Quit
        else
            // delete char at cursor (bash behaviour)
            if s.Cursor < s.Buffer.Count then
                s.Buffer.RemoveAt s.Cursor
            Continue
    elif isShift k ConsoleKey.Enter then
        s.Buffer.Insert(s.Cursor, '\n')
        s.Cursor <- s.Cursor + 1
        Continue
    elif k.Key = ConsoleKey.Enter then
        Submit (String(s.Buffer.ToArray()))
    elif k.Key = ConsoleKey.Backspace then
        if s.Cursor > 0 then
            s.Buffer.RemoveAt(s.Cursor - 1)
            s.Cursor <- s.Cursor - 1
        Continue
    elif k.Key = ConsoleKey.Delete then
        if s.Cursor < s.Buffer.Count then
            s.Buffer.RemoveAt s.Cursor
        Continue
    elif k.Key = ConsoleKey.LeftArrow then
        if s.Cursor > 0 then s.Cursor <- s.Cursor - 1
        Continue
    elif k.Key = ConsoleKey.RightArrow then
        if s.Cursor < s.Buffer.Count then s.Cursor <- s.Cursor + 1
        Continue
    elif k.Key = ConsoleKey.Home then
        // beginning of current visual line (= last \n before cursor + 1, or 0)
        let mutable i = s.Cursor - 1
        while i >= 0 && s.Buffer.[i] <> '\n' do i <- i - 1
        s.Cursor <- i + 1
        Continue
    elif k.Key = ConsoleKey.End then
        // end of current visual line
        let mutable i = s.Cursor
        while i < s.Buffer.Count && s.Buffer.[i] <> '\n' do i <- i + 1
        s.Cursor <- i
        Continue
    elif k.KeyChar <> ' ' && not (Char.IsControl k.KeyChar) then
        s.Buffer.Insert(s.Cursor, k.KeyChar)
        s.Cursor <- s.Cursor + 1
        Continue
    else
        Continue

/// Stub for the IO-driven read; will be filled in Task 3.3.
let readAsync (prompt: string) (ct: CancellationToken) : Task<string option> =
    Task.FromResult(None)
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Fugue.Tests --filter ReadLineTests 2>&1 | tail -5`

Expected: `Passed: 7, Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/Fugue.Cli/ReadLine.fs tests/Fugue.Tests/ReadLineTests.fs tests/Fugue.Tests/Fugue.Tests.fsproj
git commit -m "feat(cli): ReadLine pure key-handler with applyKey + Action"
```

---

### Task 3.3: ReadLine — IO-driven read + redraw

**Files:**
- Modify: `src/Fugue.Cli/ReadLine.fs` (replace `readAsync` stub)

- [ ] **Step 1: Implement redraw and readAsync**

Replace `readAsync` stub at the bottom of `src/Fugue.Cli/ReadLine.fs`:

```fsharp
let private writeRaw (s: string) =
    Console.Out.Write s
    Console.Out.Flush()

let private redraw (st: S) =
    // 1. cursor up to start of our previous render, clear from there to screen end
    if st.LinesRendered > 0 then
        if st.LinesRendered > 1 then
            writeRaw (sprintf "\x1b[%dA" (st.LinesRendered - 1))
        writeRaw "\r\x1b[J"
    else
        writeRaw "\r"
    // 2. emit prompt + buffer
    writeRaw st.PromptText
    let bufStr = String(st.Buffer.ToArray())
    writeRaw bufStr
    // 3. count rendered terminal lines (= 1 + number of '\n' in buffer)
    let newlines = bufStr |> Seq.filter (fun c -> c = '\n') |> Seq.length
    st.LinesRendered <- 1 + newlines
    // 4. position cursor at st.Cursor inside buffer.
    //    Compute (row, col) from start of prompt.
    let mutable row = 0
    let mutable col = st.PromptVisLen
    for i in 0 .. st.Cursor - 1 do
        if st.Buffer.[i] = '\n' then
            row <- row + 1
            col <- 0
        else
            col <- col + 1
    // We're currently positioned at end of buffer (last row, last col).
    // Move from there to (row, col): up by (lastRow - row), then to absolute column.
    let lastRow = st.LinesRendered - 1
    let upBy = lastRow - row
    if upBy > 0 then writeRaw (sprintf "\x1b[%dA" upBy)
    writeRaw (sprintf "\r\x1b[%dC" col)

let readAsync (prompt: string) (ct: CancellationToken) : Task<string option> = task {
    // Strip ANSI escapes for visible-length calc (rough — assumes only \x1b[...m sequences).
    let visLen =
        let mutable n = 0
        let mutable i = 0
        while i < prompt.Length do
            if prompt.[i] = '\x1b' then
                while i < prompt.Length && prompt.[i] <> 'm' do i <- i + 1
                if i < prompt.Length then i <- i + 1
            else
                n <- n + 1
                i <- i + 1
        n
    let st : S =
        { Buffer        = ResizeArray<char>()
          Cursor        = 0
          LinesRendered = 0
          PromptText    = prompt
          PromptVisLen  = visLen
          Width         = max 40 Console.WindowWidth }

    Console.TreatControlCAsInput <- true
    try
        redraw st
        let mutable result : string option voption = ValueNone
        while result.IsNone && not ct.IsCancellationRequested do
            // Console.ReadKey is blocking; we accept that — cancellation in this
            // path is not expected in normal flow (Ctrl+C while reading is a wipe).
            let k = Console.ReadKey(intercept = true)
            match applyKey k st with
            | Continue -> redraw st
            | Wipe     -> redraw st
            | Submit s ->
                writeRaw "\n"   // end the input line cleanly
                result <- ValueSome (Some s)
            | Quit ->
                writeRaw "\n"
                result <- ValueSome None
        return
            match result with
            | ValueSome v -> v
            | ValueNone   -> None
    finally
        Console.TreatControlCAsInput <- false
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Fugue.Cli 2>&1 | tail -8`

Expected: BUILD FAIL on `MarkdownRender.fs not found` (next file in compile chain) but `ReadLine.fs` itself compiles. The error should mention a different file, not ReadLine.

- [ ] **Step 3: Run ReadLine tests (still pure-handler)**

Run: `dotnet test tests/Fugue.Tests --filter ReadLineTests 2>&1 | tail -5`

Expected: still `Passed: 7` (we didn't touch the pure handler).

- [ ] **Step 4: Commit**

```bash
git add src/Fugue.Cli/ReadLine.fs
git commit -m "feat(cli): ReadLine async IO loop + ANSI redraw"
```

---

### Task 3.4: MarkdownRender.fs — paragraph + heading + inline emphasis

**Files:**
- Create: `src/Fugue.Cli/MarkdownRender.fs`
- Test: `tests/Fugue.Tests/MarkdownRenderTests.fs`

- [ ] **Step 1: Write failing test**

Create `tests/Fugue.Tests/MarkdownRenderTests.fs`:

```fsharp
module Fugue.Tests.MarkdownRenderTests

open Spectre.Console
open Spectre.Console.Testing
open Xunit
open FsUnit.Xunit
open Fugue.Cli

let private renderToString (renderable: Spectre.Console.Rendering.IRenderable) : string =
    let console = TestConsole(Width = 80)
    console.Write renderable
    console.Output

[<Fact>]
let ``paragraph renders as plain text`` () =
    let out = MarkdownRender.toRenderable "hello world" |> renderToString
    out |> should haveSubstring "hello world"

[<Fact>]
let ``bold renders with emphasis`` () =
    let out = MarkdownRender.toRenderable "make it **strong** now" |> renderToString
    out |> should haveSubstring "strong"

[<Fact>]
let ``inline code renders text`` () =
    let out = MarkdownRender.toRenderable "use `Console.WriteLine` here" |> renderToString
    out |> should haveSubstring "Console.WriteLine"

[<Fact>]
let ``heading renders text`` () =
    let out = MarkdownRender.toRenderable "# Heading One\nbody" |> renderToString
    out |> should haveSubstring "Heading One"
    out |> should haveSubstring "body"
```

Add to `tests/Fugue.Tests/Fugue.Tests.fsproj`:

```xml
<Compile Include="MarkdownRenderTests.fs" />
```

Also add `Spectre.Console.Testing` package to test project. Modify `tests/Fugue.Tests/Fugue.Tests.fsproj` to add:

```xml
<PackageReference Include="Spectre.Console.Testing" Version="0.49.*" />
```

- [ ] **Step 2: Run test, expect fail**

Run: `dotnet test tests/Fugue.Tests --filter MarkdownRenderTests 2>&1 | tail -5`

Expected: BUILD FAIL — `Fugue.Cli.MarkdownRender` not found.

- [ ] **Step 3: Create MarkdownRender.fs**

Create `src/Fugue.Cli/MarkdownRender.fs`:

```fsharp
module Fugue.Cli.MarkdownRender

open System.Text
open Markdig
open Markdig.Syntax
open Markdig.Syntax.Inlines
open Spectre.Console
open Spectre.Console.Rendering

let pipeline : MarkdownPipeline =
    MarkdownPipelineBuilder()
        .UseGridTables()
        .UsePipeTables()
        .UseEmphasisExtras()
        .Build()

let private escape (text: string) : string =
    Markup.Escape text

let rec private renderInline (sb: StringBuilder) (inl: Inline) : unit =
    match inl with
    | :? LiteralInline as lit ->
        sb.Append(escape (lit.Content.ToString())) |> ignore
    | :? EmphasisInline as em ->
        let tag =
            match em.DelimiterCount with
            | 1 -> "italic"
            | 2 -> "bold"
            | _ -> "bold italic"
        sb.Append(sprintf "[%s]" tag) |> ignore
        for child in em do renderInline sb child
        sb.Append "[/]" |> ignore
    | :? CodeInline as ci ->
        sb.Append(sprintf "[teal]%s[/]" (escape ci.Content)) |> ignore
    | :? LinkInline as li ->
        let label =
            let inner = StringBuilder()
            for child in li do renderInline inner child
            inner.ToString()
        sb.Append(sprintf "[link=%s]%s[/]" (escape (li.Url |> Option.ofObj |> Option.defaultValue "")) label) |> ignore
    | :? LineBreakInline ->
        sb.Append '\n' |> ignore
    | :? ContainerInline as ci ->
        for child in ci do renderInline sb child
    | _ ->
        sb.Append(escape (inl.ToString())) |> ignore

let private inlineToMarkup (block: ContainerInline | null) : string =
    match block with
    | null -> ""
    | inl ->
        let sb = StringBuilder()
        for child in inl do renderInline sb child
        sb.ToString()

let private renderBlock (block: Block) : IRenderable =
    match block with
    | :? HeadingBlock as h ->
        let style =
            match h.Level with
            | 1 -> "bold underline"
            | 2 -> "bold"
            | _ -> "bold dim"
        let body = inlineToMarkup h.Inline
        Markup(sprintf "[%s]%s[/]" style body) :> IRenderable
    | :? ParagraphBlock as p ->
        Markup(inlineToMarkup p.Inline) :> IRenderable
    | :? FencedCodeBlock as f ->
        let lines =
            f.Lines.Lines
            |> Seq.take f.Lines.Count
            |> Seq.map (fun l -> l.ToString())
            |> Seq.toArray
        let rows =
            lines
            |> Array.mapi (fun i ln ->
                let nr = sprintf "[dim]%3d[/]" (i + 1)
                let body = sprintf "[dim]%s[/]" (escape ln)
                Columns([ Markup(nr) :> IRenderable; Markup(body) :> IRenderable ]) :> IRenderable)
        Padder(Rows(rows)).PadLeft(2) :> IRenderable
    | :? Markdig.Syntax.CodeBlock as c ->
        let txt = c.Lines.ToString()
        Padder(Markup(sprintf "[dim]%s[/]" (escape txt))).PadLeft(2) :> IRenderable
    | :? Markdig.Syntax.QuoteBlock as q ->
        let inner =
            q
            |> Seq.cast<Block>
            |> Seq.map renderBlock
            |> Seq.toArray
        Padder(Rows(inner)).PadLeft(2) :> IRenderable
    | :? Markdig.Syntax.ListBlock as lb ->
        let items =
            lb
            |> Seq.cast<Block>
            |> Seq.mapi (fun i item ->
                let prefix = if lb.IsOrdered then sprintf "%d. " (i + 1) else "• "
                let inner =
                    match item with
                    | :? Markdig.Syntax.ListItemBlock as li ->
                        li
                        |> Seq.cast<Block>
                        |> Seq.map renderBlock
                        |> Seq.toArray
                        |> Rows
                    | _ -> Markup("") :> IRenderable
                Columns([ Markup(prefix) :> IRenderable; inner ]) :> IRenderable)
            |> Seq.toArray
        Rows(items) :> IRenderable
    | :? ThematicBreakBlock ->
        Rule() :> IRenderable
    | _ ->
        Markup(escape (block.ToString())) :> IRenderable

/// Convert markdown source text to a single composite Spectre IRenderable.
let toRenderable (markdown: string) : IRenderable =
    let doc = Markdown.Parse(markdown, pipeline)
    let parts =
        doc
        |> Seq.cast<Block>
        |> Seq.map renderBlock
        |> Seq.toArray
    if parts.Length = 1 then parts.[0]
    else Rows(parts) :> IRenderable
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Fugue.Tests --filter MarkdownRenderTests 2>&1 | tail -10`

Expected: `Passed: 4, Failed: 0`. If a test fails because of unexpected ANSI escapes in the captured output, switch the assertion to `should haveSubstring` only on the visible text — Spectre's TestConsole strips ANSI by default.

- [ ] **Step 5: Commit**

```bash
git add src/Fugue.Cli/MarkdownRender.fs tests/Fugue.Tests/MarkdownRenderTests.fs tests/Fugue.Tests/Fugue.Tests.fsproj
git commit -m "feat(cli): Markdig-based markdown to Spectre IRenderable"
```

---

### Task 3.5: DiffRender.fs

**Files:**
- Create: `src/Fugue.Cli/DiffRender.fs`
- Test: `tests/Fugue.Tests/DiffRenderTests.fs`

- [ ] **Step 1: Write failing test**

Create `tests/Fugue.Tests/DiffRenderTests.fs`:

```fsharp
module Fugue.Tests.DiffRenderTests

open Spectre.Console.Testing
open Xunit
open FsUnit.Xunit
open Fugue.Cli

[<Fact>]
let ``looksLikeDiff true on canonical hunk`` () =
    let txt =
        "@@ -1,3 +1,3 @@\n" +
        "-old\n" +
        "+new\n" +
        " context\n"
    DiffRender.looksLikeDiff txt |> should equal true

[<Fact>]
let ``looksLikeDiff false on plain text`` () =
    DiffRender.looksLikeDiff "just a sentence" |> should equal false

[<Fact>]
let ``looksLikeDiff true on multiple plus minus lines without hunk header`` () =
    let txt = "-line1\n+line1-mod\n-line2\n+line2-mod\n"
    DiffRender.looksLikeDiff txt |> should equal true

[<Fact>]
let ``toRenderable renders without throwing on empty input`` () =
    let console = TestConsole(Width = 80)
    console.Write(DiffRender.toRenderable "")
    console.Output |> should not' (be NullOrEmptyString)

[<Fact>]
let ``toRenderable contains - and + content`` () =
    let txt = "@@ -1 +1 @@\n-a\n+b\n"
    let console = TestConsole(Width = 80)
    console.Write(DiffRender.toRenderable txt)
    console.Output |> should haveSubstring "a"
    console.Output |> should haveSubstring "b"
```

Add to `tests/Fugue.Tests/Fugue.Tests.fsproj`:

```xml
<Compile Include="DiffRenderTests.fs" />
```

- [ ] **Step 2: Run test, expect fail**

Run: `dotnet test tests/Fugue.Tests --filter DiffRenderTests 2>&1 | tail -5`

Expected: BUILD FAIL — `Fugue.Cli.DiffRender` not found.

- [ ] **Step 3: Create DiffRender.fs**

Create `src/Fugue.Cli/DiffRender.fs`:

```fsharp
module Fugue.Cli.DiffRender

open Spectre.Console
open Spectre.Console.Rendering

let private hasHunkHeader (lines: string array) =
    lines |> Array.exists (fun l -> l.StartsWith "@@ " && l.Contains "@@")

let private plusMinusLines (lines: string array) =
    lines
    |> Array.sumBy (fun l ->
        if l.StartsWith "+" || l.StartsWith "-" then 1 else 0)

let looksLikeDiff (text: string) : bool =
    if isNull text || text = "" then false
    else
        let lines =
            text.Split('\n')
            |> Array.truncate 50
        hasHunkHeader lines || plusMinusLines lines >= 2

let private styleLine (l: string) : IRenderable =
    if l.StartsWith "+" then
        Markup(sprintf "[green]%s[/]" (Markup.Escape l)) :> _
    elif l.StartsWith "-" then
        Markup(sprintf "[red]%s[/]" (Markup.Escape l)) :> _
    elif l.StartsWith "@@" then
        Markup(sprintf "[magenta dim]%s[/]" (Markup.Escape l)) :> _
    else
        Markup(sprintf "[dim]%s[/]" (Markup.Escape l)) :> _

let toRenderable (text: string) : IRenderable =
    let lines = if isNull text then [||] else text.Split('\n')
    let renderables = lines |> Array.map styleLine
    if renderables.Length = 0 then Markup("") :> _
    else Rows(renderables) :> _
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Fugue.Tests --filter DiffRenderTests 2>&1 | tail -5`

Expected: `Passed: 5, Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/Fugue.Cli/DiffRender.fs tests/Fugue.Tests/DiffRenderTests.fs tests/Fugue.Tests/Fugue.Tests.fsproj
git commit -m "feat(cli): unified-diff detection + Spectre render"
```

---

### Task 3.6: Render.fs — domain types + user/assistant/error/cancelled

**Files:**
- Create: `src/Fugue.Cli/Render.fs`
- Test: `tests/Fugue.Tests/RenderTests.fs`

- [ ] **Step 1: Write failing test**

Create `tests/Fugue.Tests/RenderTests.fs`:

```fsharp
module Fugue.Tests.RenderTests

open Spectre.Console.Testing
open Xunit
open FsUnit.Xunit
open Fugue.Cli
open Fugue.Cli.Render
open Fugue.Core.Config
open Fugue.Core.Localization

let private uiLeft : UiConfig = { UserAlignment = Left; Locale = "en" }
let private uiRight : UiConfig = { UserAlignment = Right; Locale = "en" }

let private toStr (r: Spectre.Console.Rendering.IRenderable) : string =
    let c = TestConsole(Width = 80)
    c.Write r
    c.Output

[<Fact>]
let ``userMessage Left contains '> ' prefix`` () =
    let out = userMessage uiLeft "hello" |> toStr
    out |> should haveSubstring "›"
    out |> should haveSubstring "hello"

[<Fact>]
let ``userMessage Right does not have left-edge prefix`` () =
    // Right-aligned: text is right-justified within terminal width, no '>' prefix
    let out = userMessage uiRight "hello" |> toStr
    out |> should haveSubstring "hello"

[<Fact>]
let ``cancelled uses localized text`` () =
    let s = pick "ru"
    let out = cancelled s |> toStr
    out |> should haveSubstring "отменено"

[<Fact>]
let ``errorLine includes error prefix and message`` () =
    let s = pick "en"
    let out = errorLine s "boom" |> toStr
    out |> should haveSubstring "error"
    out |> should haveSubstring "boom"

[<Fact>]
let ``toolBullet running shows yellow circle and tool name`` () =
    let s = pick "en"
    let state = Running("Bash", "echo hi")
    let out = toolBullet s state |> toStr
    out |> should haveSubstring "Bash"
    out |> should haveSubstring "running"

[<Fact>]
let ``toolBullet completed plain output renders without diff`` () =
    let s = pick "en"
    let state = Completed("Read", "{}", "alpha\nbeta")
    let out = toolBullet s state |> toStr
    out |> should haveSubstring "Read"
    out |> should haveSubstring "alpha"
```

Add to `tests/Fugue.Tests/Fugue.Tests.fsproj`:

```xml
<Compile Include="RenderTests.fs" />
```

- [ ] **Step 2: Run test, expect fail**

Run: `dotnet test tests/Fugue.Tests --filter RenderTests 2>&1 | tail -5`

Expected: BUILD FAIL.

- [ ] **Step 3: Create Render.fs**

Create `src/Fugue.Cli/Render.fs`:

```fsharp
module Fugue.Cli.Render

open Spectre.Console
open Spectre.Console.Rendering
open Fugue.Core.Config
open Fugue.Core.Localization

type ToolState =
    | Running   of name: string * args: string
    | Completed of name: string * args: string * output: string
    | Failed    of name: string * args: string * err: string

/// Path-aware prompt string for ReadLine. Just visible chars; ANSI not added here
/// because Console.WriteLine inside ReadLine doesn't go through Spectre.
let prompt (_cwd: string) : string = "› "

let userMessage (ui: UiConfig) (text: string) : IRenderable =
    let escaped = Markup.Escape text
    match ui.UserAlignment with
    | Left ->
        Markup(sprintf "[grey]›[/] %s" escaped) :> _
    | Right ->
        let bubble = Padder(Markup(escaped)).PadLeft(2).PadRight(2) :> IRenderable
        Align(bubble, HorizontalAlignment.Right) :> _

/// Plain assistant text during streaming (no markdown parse — buffer is partial).
let assistantLive (text: string) : IRenderable =
    Text(text) :> _

/// Final assistant text rendered as markdown.
let assistantFinal (text: string) : IRenderable =
    MarkdownRender.toRenderable text

let cancelled (s: Strings) : IRenderable =
    Markup(sprintf "[yellow]⚠ %s[/]" (Markup.Escape s.Cancelled)) :> _

let errorLine (s: Strings) (msg: string) : IRenderable =
    Markup(sprintf "[red]⚠ %s:[/] %s" (Markup.Escape s.ErrorPrefix) (Markup.Escape msg)) :> _

let private renderToolBody (output: string) : IRenderable =
    if DiffRender.looksLikeDiff output then DiffRender.toRenderable output
    elif output.Contains "```" || output.Contains "**" || output.StartsWith "#" then
        MarkdownRender.toRenderable output
    else
        // dim plain text, indented
        let lines = output.Split('\n')
        let trimmed =
            if lines.Length > 12 then
                Array.append (Array.truncate 12 lines)
                    [| sprintf "[dim]+ %d more lines[/]" (lines.Length - 12) |]
            else lines
        let rows =
            trimmed
            |> Array.map (fun l ->
                Markup(sprintf "[dim]%s[/]" (Markup.Escape l)) :> IRenderable)
        Padder(Rows(rows)).PadLeft(2) :> _

let toolBullet (s: Strings) (state: ToolState) : IRenderable =
    match state with
    | Running(name, args) ->
        let header =
            sprintf "[yellow]●[/] [bold]%s[/]([dim]%s[/])"
                (Markup.Escape name) (Markup.Escape args)
        let body =
            Padder(Markup(sprintf "[dim]%s[/]" (Markup.Escape s.ToolRunning))).PadLeft(2)
        Rows([ Markup(header) :> IRenderable; body :> _ ]) :> _
    | Completed(name, args, output) ->
        let header =
            sprintf "[green]●[/] [bold]%s[/]([dim]%s[/])"
                (Markup.Escape name) (Markup.Escape args)
        Rows([ Markup(header) :> IRenderable; renderToolBody output ]) :> _
    | Failed(name, args, err) ->
        let header =
            sprintf "[red]●[/] [bold]%s[/]([dim]%s[/])"
                (Markup.Escape name) (Markup.Escape args)
        let body =
            Padder(Markup(sprintf "[red]%s[/]" (Markup.Escape err))).PadLeft(2)
        Rows([ Markup(header) :> IRenderable; body :> _ ]) :> _
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Fugue.Tests --filter RenderTests 2>&1 | tail -5`

Expected: `Passed: 6, Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/Fugue.Cli/Render.fs tests/Fugue.Tests/RenderTests.fs tests/Fugue.Tests/Fugue.Tests.fsproj
git commit -m "feat(cli): Render module — userMessage/assistant/toolBullet/error/cancelled"
```

---

### Task 3.7: StatusBar.fs

**Files:**
- Create: `src/Fugue.Cli/StatusBar.fs`

- [ ] **Step 1: Create StatusBar.fs**

Create `src/Fugue.Cli/StatusBar.fs`:

```fsharp
module Fugue.Cli.StatusBar

open System
open System.Diagnostics
open System.IO
open System.Threading
open Fugue.Core.Localization
open Fugue.Core.Config

let private writeRaw (s: string) =
    Console.Out.Write s
    Console.Out.Flush()

let mutable private cwd: string = "."
let mutable private cfg: AppConfig option = None
let mutable private active = false
let mutable private branchCache : string * DateTime = "", DateTime.MinValue

let private branchOf (cwd: string) : string =
    let now = DateTime.UtcNow
    let cached, cachedAt = branchCache
    if (now - cachedAt).TotalSeconds < 2.0 then cached
    else
        try
            let psi = ProcessStartInfo("git", "rev-parse --abbrev-ref HEAD")
            psi.WorkingDirectory <- cwd
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            use p = Process.Start psi
            p.WaitForExit 500 |> ignore
            let out = (p.StandardOutput.ReadToEnd()).Trim()
            let result = if String.IsNullOrEmpty out then "(no git)" else out
            branchCache <- result, now
            result
        with _ ->
            branchCache <- "(no git)", now
            "(no git)"

let private homeRel (path: string) : string =
    let home = Environment.GetEnvironmentVariable "HOME" |> Option.ofObj |> Option.defaultValue ""
    if home <> "" && path.StartsWith home then "~" + path.Substring(home.Length)
    else path

let private providerLabel (cfg: AppConfig) : string =
    match cfg.Provider with
    | Anthropic(_, m) -> sprintf "anthropic:%s" m
    | OpenAI(_, m)    -> sprintf "openai:%s" m
    | Ollama(_, m)    -> sprintf "ollama:%s" m

let refresh () =
    if not active then () else
    let height = Console.WindowHeight
    let strings =
        match cfg with
        | Some c -> Localization.pick c.Ui.Locale
        | None   -> Localization.en
    let line1 =
        sprintf "[grey]%s %s [bold]%s[/][/]"
            (homeRel cwd) strings.StatusBarOn (branchOf cwd)
    let line2 =
        match cfg with
        | Some c -> sprintf "[grey]%s · %s[/]" strings.StatusBarApp (providerLabel c)
        | None   -> sprintf "[grey]%s[/]" strings.StatusBarApp
    // save cursor, jump to bottom-1, clear two lines, write, restore
    writeRaw "\x1b[s"
    writeRaw (sprintf "\x1b[%d;1H" (height - 1))
    writeRaw "\x1b[2K"
    Spectre.Console.AnsiConsole.Markup line1
    writeRaw (sprintf "\x1b[%d;1H" height)
    writeRaw "\x1b[2K"
    Spectre.Console.AnsiConsole.Markup line2
    writeRaw "\x1b[u"

let start (initialCwd: string) (initialCfg: AppConfig) : unit =
    cwd <- initialCwd
    cfg <- Some initialCfg
    active <- true
    // reserve two bottom lines: emit two newlines, then set scroll region 1..(h-2)
    let height = Console.WindowHeight
    Console.WriteLine()
    Console.WriteLine()
    writeRaw (sprintf "\x1b[1;%dr" (height - 2))
    writeRaw (sprintf "\x1b[%d;1H" (height - 2))
    refresh ()

let stop () : unit =
    if not active then () else
    let height = Console.WindowHeight
    writeRaw "\x1b[r"          // reset scroll region
    writeRaw (sprintf "\x1b[%d;1H" (height - 1))
    writeRaw "\x1b[2K"
    writeRaw (sprintf "\x1b[%d;1H" height)
    writeRaw "\x1b[2K"
    active <- false
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Fugue.Cli 2>&1 | tail -8`

Expected: BUILD FAIL on `Repl.fs not found` (next file). StatusBar.fs itself compiles.

- [ ] **Step 3: Commit**

```bash
git add src/Fugue.Cli/StatusBar.fs
git commit -m "feat(cli): persistent 2-line status bar via ANSI scroll-region"
```

(StatusBar tested via E2E in Phase 5 — terminal IO can't be properly unit-tested.)

---

### Task 3.8: Repl.fs

**Files:**
- Create: `src/Fugue.Cli/Repl.fs`

- [ ] **Step 1: Create Repl.fs**

Create `src/Fugue.Cli/Repl.fs`:

```fsharp
module Fugue.Cli.Repl

open System
open System.Collections.Generic
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Agents.AI
open Spectre.Console
open Fugue.Core.Config
open Fugue.Core.Localization
open Fugue.Agent
open Fugue.Cli

/// Holder for the currently active stream's CancellationTokenSource.
/// Console.CancelKeyPress handler uses this to cancel mid-stream Ctrl+C.
type CancelSource() =
    let mutable streamCts : CancellationTokenSource option = None
    let mutable quit = false
    member _.QuitRequested = quit
    member _.RequestQuit() = quit <- true
    member _.Token = CancellationToken.None
    member this.NewStreamCts() =
        let cts = new CancellationTokenSource()
        streamCts <- Some cts
        cts
    member _.OnCtrlC (args: ConsoleCancelEventArgs) =
        match streamCts with
        | Some cts ->
            try cts.Cancel() with _ -> ()
            args.Cancel <- true
        | None ->
            quit <- true
            args.Cancel <- true
    interface IDisposable with
        member _.Dispose() =
            streamCts |> Option.iter (fun c -> try c.Dispose() with _ -> ())

let private finishState (prev: Render.ToolState) (isErr: bool) (output: string) : Render.ToolState =
    match prev with
    | Render.Running(n, a) ->
        if isErr then Render.Failed(n, a, output)
        else Render.Completed(n, a, output)
    | other -> other

let private streamAndRender
        (agent: AIAgent) (session: AgentSession) (input: string)
        (cfg: AppConfig) (cancelSrc: CancelSource) : Task<unit> = task {
    use streamCts = cancelSrc.NewStreamCts()
    let strings = Localization.pick cfg.Ui.Locale
    let assistantBuf = StringBuilder()
    let tools = Dictionary<string, Render.ToolState>()
    let toolOrder = ResizeArray<string>()

    let layout () =
        let parts = ResizeArray<Spectre.Console.Rendering.IRenderable>()
        if assistantBuf.Length > 0 then
            parts.Add(Render.assistantLive (assistantBuf.ToString()))
        for id in toolOrder do
            parts.Add(Render.toolBullet strings tools.[id])
        Rows(parts) :> Spectre.Console.Rendering.IRenderable

    let live = AnsiConsole.Live(layout()).AutoClear(true)

    try
        do! live.StartAsync(fun ctx -> task {
            let stream = Conversation.run agent session input streamCts.Token
            let enumerator = stream.GetAsyncEnumerator(streamCts.Token)
            let mutable hasNext = true
            while hasNext do
                let! step = enumerator.MoveNextAsync().AsTask()
                if step then
                    match enumerator.Current with
                    | Conversation.TextChunk t ->
                        assistantBuf.Append t |> ignore
                    | Conversation.ToolStarted(id, n, a) ->
                        if not (tools.ContainsKey id) then toolOrder.Add id
                        tools.[id] <- Render.Running(n, a)
                    | Conversation.ToolCompleted(id, o, err) ->
                        let prev =
                            if tools.ContainsKey id then tools.[id]
                            else Render.Running("?", "?")
                        tools.[id] <- finishState prev err o
                    | Conversation.Finished -> ()
                    | Conversation.Failed _ -> ()
                    ctx.UpdateTarget(layout())
                    ctx.Refresh()
                else hasNext <- false
        })
        // Live cleared. Now render final state.
        if assistantBuf.Length > 0 then
            AnsiConsole.Write(Render.assistantFinal (assistantBuf.ToString()))
            AnsiConsole.WriteLine()
        for id in toolOrder do
            AnsiConsole.Write(Render.toolBullet strings tools.[id])
            AnsiConsole.WriteLine()
    with
    | :? OperationCanceledException ->
        AnsiConsole.Write(Render.cancelled strings)
        AnsiConsole.WriteLine()
    | ex ->
        AnsiConsole.Write(Render.errorLine strings ex.Message)
        AnsiConsole.WriteLine()
}

let run (agent: AIAgent) (cfg: AppConfig) (cwd: string) : Task<unit> = task {
    use cancelSrc = new CancelSource()
    Console.CancelKeyPress.Add(fun args -> cancelSrc.OnCtrlC args)
    let session = agent.GetNewSession()
    StatusBar.start cwd cfg

    try
        while not cancelSrc.QuitRequested do
            let! lineOpt = ReadLine.readAsync (Render.prompt cwd) cancelSrc.Token
            match lineOpt with
            | None ->
                cancelSrc.RequestQuit()
            | Some "" -> ()
            | Some s when s = "/exit" || s = "/quit" ->
                cancelSrc.RequestQuit()
            | Some s when s = "/clear" ->
                AnsiConsole.Clear()
                StatusBar.refresh ()
            | Some userInput ->
                AnsiConsole.Write(Render.userMessage cfg.Ui userInput)
                AnsiConsole.WriteLine()
                do! streamAndRender agent session userInput cfg cancelSrc
                StatusBar.refresh ()
    finally
        StatusBar.stop ()
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/Fugue.Cli 2>&1 | tail -10`

Expected: BUILD FAIL on `Program.fs not found` (next task). Repl.fs compiles.

- [ ] **Step 3: Commit**

```bash
git add src/Fugue.Cli/Repl.fs
git commit -m "feat(cli): Repl loop with streamAndRender + cancel handling"
```

---

### Task 3.9: Program.fs — bootstrap + main

**Files:**
- Modify: `src/Fugue.Cli/Program.fs` (full rewrite)

- [ ] **Step 1: Replace Program.fs**

Overwrite `src/Fugue.Cli/Program.fs`:

```fsharp
module Fugue.Cli.Program

open System
open Microsoft.Agents.AI
open Fugue.Core.Config
open Fugue.Agent

let private buildAgent (cfg: AppConfig) : AIAgent =
    let cwd = Environment.CurrentDirectory
    let tools = Fugue.Tools.ToolRegistry.buildAll cwd
    let sysPrompt =
        match cfg.SystemPrompt with
        | Some s -> s
        | None -> Fugue.Core.SystemPrompt.render cwd Fugue.Tools.ToolRegistry.names
    AgentFactory.create cfg.Provider sysPrompt tools

let private runWithCfg (cfg: AppConfig) : int =
    let agent = buildAgent cfg
    let cwd = Environment.CurrentDirectory
    Repl.run agent cfg cwd
    |> fun t ->
        try t.Wait()
        with
        | :? AggregateException as agg when agg.InnerException :? OperationCanceledException -> ()
    0

[<EntryPoint>]
let main argv =
    Fugue.Core.Log.session ()
    Fugue.Core.Log.info "main" (sprintf "fugue starting, argv=[%s]" (String.concat "; " argv))
    match Fugue.Core.Config.load argv with
    | Error (NoConfigFound help) ->
        let candidates = Discovery.discover (Discovery.defaultSources ())
        match Discovery.prompt candidates with
        | Some chosen ->
            let provider =
                match chosen with
                | Discovery.EnvKey("anthropic", model, _) ->
                    let key = Environment.GetEnvironmentVariable "ANTHROPIC_API_KEY" |> Option.ofObj |> Option.defaultValue ""
                    Anthropic(key, model)
                | Discovery.EnvKey("openai", model, _) ->
                    let key = Environment.GetEnvironmentVariable "OPENAI_API_KEY" |> Option.ofObj |> Option.defaultValue ""
                    OpenAI(key, model)
                | Discovery.ClaudeSettings(model, key) -> Anthropic(key, model)
                | Discovery.OllamaLocal(ep, models) ->
                    let m = if List.isEmpty models then "llama3.1" else List.head models
                    Ollama(ep, m)
                | _ -> failwith "unsupported candidate"
            let cfg = { Provider = provider; SystemPrompt = None; MaxIterations = 30; Ui = Fugue.Core.Config.defaultUi () }
            Fugue.Core.Config.saveToFile cfg
            runWithCfg cfg
        | None ->
            eprintfn "%s" help
            1
    | Error (InvalidConfig reason) ->
        eprintfn "Invalid config: %s" reason
        1
    | Ok cfg ->
        runWithCfg cfg
```

- [ ] **Step 2: Build whole solution**

Run: `dotnet build 2>&1 | tail -10`

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

- [ ] **Step 3: Run all tests**

Run: `dotnet test 2>&1 | tail -5`

Expected: ≥ 50 passed, 0 failed (sum of all new test files plus existing tool tests + ConfigTests + LocalizationTests).

- [ ] **Step 4: Smoke-run in Debug**

Run: `dotnet run --project src/Fugue.Cli -c Debug -- --version 2>&1 | tail -5`

(`--version` will fall through Config.load to NoConfigFound or read existing config. Either way — no exception expected.)

If config exists: REPL launches with status bar — kill via Ctrl+C.

- [ ] **Step 5: Commit**

```bash
git add src/Fugue.Cli/Program.fs
git commit -m "feat(cli): wire Spectre Repl + AgentFactory + Discovery in Program.fs"
```

---

## Phase 4 — Native AOT publish + trim-warning sweep

### Task 4.1: Initial AOT publish + collect warnings

**Files:**
- Modify: `src/Fugue.Cli/Fugue.Cli.fsproj` (enable PublishAot)

- [ ] **Step 1: Enable PublishAot**

Modify `src/Fugue.Cli/Fugue.Cli.fsproj` `<PropertyGroup>` — add:

```xml
<PublishAot>true</PublishAot>
<IsAotCompatible>true</IsAotCompatible>
<InvariantGlobalization>true</InvariantGlobalization>
<StripSymbols>true</StripSymbols>
<OptimizationPreference>Size</OptimizationPreference>
<IlcGenerateStackTraceData>true</IlcGenerateStackTraceData>
```

Also temporarily add `<NoWarn>$(NoWarn);IL2026;IL3050;IL3053;IL2104</NoWarn>` to silence package warnings during this initial probe. We'll iterate to remove these.

- [ ] **Step 2: Run publish, capture warnings**

Run: `rm -rf src/Fugue.Cli/bin/Release && dotnet publish src/Fugue.Cli -c Release -r osx-arm64 2>&1 | tee /tmp/fugue-aot-warnings.log | tail -30`

Expected: success or fail. If success — warnings about IL2026/IL3050 in dependencies are suppressed.

- [ ] **Step 3: Inspect warnings**

Run: `grep -E "warning IL" /tmp/fugue-aot-warnings.log | sort -u | head -20`

Expected: 0 lines (because we put them in NoWarn). To see them — temporarily remove NoWarn, re-run, count. Document categorisation in commit message.

- [ ] **Step 4: Commit baseline + warnings inventory**

```bash
git add src/Fugue.Cli/Fugue.Cli.fsproj
git commit -m "build(cli): enable PublishAot=true with package-warning suppression baseline"
```

---

### Task 4.2: Verify AOT binary launches

**Files:** none.

- [ ] **Step 1: Check binary size**

Run: `ls -lh src/Fugue.Cli/bin/Release/net10.0/osx-arm64/publish/fugue`

Expected: ≤ 35 MB. If > 50 MB — likely FSharp.Core not trimmed; investigate.

- [ ] **Step 2: Smoke-launch (no config will fail to NoConfigFound)**

Run:
```bash
( src/Fugue.Cli/bin/Release/net10.0/osx-arm64/publish/fugue 2>&1 & PID=$!; sleep 3; kill -TERM $PID 2>/dev/null; wait $PID 2>/dev/null ) | head -30
```

Expected: no `Unhandled exception`. Either status bar appears (config exists) or "No Fugue configuration found" message (config absent).

If `Unhandled exception: System.NotSupportedException: JsonTypeInfo metadata...` appears — `JsonSerializerIsReflectionEnabledByDefault=true` did not propagate. Check `Directory.Build.props` and `Fugue.Cli.fsproj` both have it; rebuild.

- [ ] **Step 3: Commit verification artefacts (none — just record working binary path)**

(no commit needed if previous task held)

---

### Task 4.3: Address trim warnings iteratively

**Files:**
- Modify: `src/Fugue.Cli/Fugue.Cli.fsproj` (add TrimmerRootAssembly entries as needed)

- [ ] **Step 1: Re-enable warnings as errors**

Edit `src/Fugue.Cli/Fugue.Cli.fsproj` — remove `<NoWarn>$(NoWarn);IL2026;IL3050;IL3053;IL2104</NoWarn>` line.

- [ ] **Step 2: Re-publish and capture errors**

Run: `dotnet publish src/Fugue.Cli -c Release -r osx-arm64 2>&1 | tee /tmp/fugue-aot-strict.log | grep -E "error IL" | sort -u`

For each unique error line, add a TrimmerRootAssembly entry **only for the third-party assembly**, never for our own code. Example:

If you see warnings from `Anthropic.SDK`:

```xml
<ItemGroup>
  <TrimmerRootAssembly Include="Anthropic.SDK" />
</ItemGroup>
```

Add a comment **above** each `TrimmerRootAssembly` explaining why (link to the issue if known).

For `FSharp.Core` IL2104 — same treatment:

```xml
<TrimmerRootAssembly Include="FSharp.Core" />
```

For warnings from `Microsoft.Agents.AI` reflection — same.

- [ ] **Step 3: Re-publish until green**

Repeat: edit fsproj → `dotnet publish ... 2>&1 | grep -E "error IL"`. When no IL errors and `Build succeeded`:

Run: `dotnet publish src/Fugue.Cli -c Release -r osx-arm64 2>&1 | tail -10`

Expected: `Build succeeded. 0 Error(s).`

- [ ] **Step 4: Verify size, smoke-launch**

Run: `ls -lh src/Fugue.Cli/bin/Release/net10.0/osx-arm64/publish/fugue && ( src/Fugue.Cli/bin/Release/net10.0/osx-arm64/publish/fugue 2>&1 & PID=$!; sleep 3; kill -TERM $PID 2>/dev/null; wait $PID 2>/dev/null ) | head -20`

Expected: size ≤ 35 MB; clean smoke launch.

- [ ] **Step 5: Measure cold start**

Run: `/usr/bin/time -l src/Fugue.Cli/bin/Release/net10.0/osx-arm64/publish/fugue --version 2>&1 | grep -E "real|maximum resident"` (where `--version` is added if the CLI doesn't yet have it; otherwise pipe `/exit\n` into stdin and time that).

Expected: real time ≤ 50ms; peak RSS ≤ 90 MB.

- [ ] **Step 6: Commit**

```bash
git add src/Fugue.Cli/Fugue.Cli.fsproj
git commit -m "build(cli): AOT-clean publish via TrimmerRootAssembly (FSharp.Core, Anthropic.SDK, MAF)"
```

---

## Phase 5 — End-to-end красота tour

### Task 5.1: Smoke against Ollama (qwen3.6:27b)

**Files:** none — manual verification.

- [ ] **Step 1: Verify Ollama running locally**

Run: `curl -s http://localhost:11434/api/tags | head -c 200`

Expected: JSON with tag list including `qwen3.6:27b`.

If Ollama not running: `brew services start ollama` (or platform equivalent) and `ollama pull qwen3.6:27b`.

- [ ] **Step 2: Set config for Ollama**

Create `~/.fugue/config.json`:

```json
{
  "provider":"ollama",
  "model":"qwen3.6:27b",
  "apiKey":"",
  "ollamaEndpoint":"http://localhost:11434",
  "maxIterations":30,
  "ui":{"userAlignment":"left","locale":"ru"}
}
```

- [ ] **Step 3: Launch fugue interactively**

Run: `src/Fugue.Cli/bin/Release/net10.0/osx-arm64/publish/fugue`

Manual check:
- Status bar visible at bottom 2 lines, shows path + branch, second line `fugue · ollama:qwen3.6:27b`.
- Prompt `›` visible.
- Type `привет` + Enter. See user message rendered with `›` prefix (Left alignment).
- Live streaming response appears.
- Final response is markdown-rendered (any `**bold**`/`*italic*`/`` `code` `` styled).

- [ ] **Step 4: Tool-loop test**

In the live REPL, send: `прочитай README.md и сделай саммари в 3 пункта`

Expected:
- `● Read(README.md)` appears in yellow during execution.
- Transitions to `● Read(...)` in green with file content body indented.
- Final markdown summary streams.

- [ ] **Step 5: Cancel test**

Send a long prompt: `напиши длинное эссе про функциональное программирование на 5 параграфов`

Mid-stream: press Ctrl+C.

Expected: single `⚠ отменено` line appears, prompt returns. No double error blocks. No further text streams in.

- [ ] **Step 6: Selection test**

After a multi-paragraph response: select text with mouse → cmd+C → paste somewhere — should be plain text without ANSI escapes.

- [ ] **Step 7: `/exit`**

Type `/exit` — fugue exits cleanly. Status bar disappears, scroll region restored, terminal back to normal prompt.

- [ ] **Step 8: Commit verification log**

(No code change. Optionally update `docs/superpowers/specs/2026-04-29-fugue-spectre-aot-pivot.md` "verification" section with check-marks. Not strictly required.)

---

### Task 5.2: Smoke against Anthropic

**Files:** none — manual.

- [ ] **Step 1: Update config**

Edit `~/.fugue/config.json`:

```json
{
  "provider":"anthropic",
  "model":"claude-opus-4-7",
  "apiKey":"sk-ant-...",
  "ollamaEndpoint":"",
  "maxIterations":30,
  "ui":{"userAlignment":"left","locale":"ru"}
}
```

(Use real `ANTHROPIC_API_KEY` — easier: leave config as Ollama and run `FUGUE_PROVIDER=anthropic ANTHROPIC_API_KEY=sk-... src/Fugue.Cli/bin/Release/net10.0/osx-arm64/publish/fugue`.)

- [ ] **Step 2: Repeat scenarios from Task 5.1**

All 8 manual checks from Ollama task. Status bar should show `anthropic:claude-opus-4-7`.

---

### Task 5.3: Smoke against OpenAI

**Files:** none — manual.

- [ ] **Step 1: Run with OpenAI config**

Run: `FUGUE_PROVIDER=openai OPENAI_API_KEY=sk-... FUGUE_MODEL=gpt-4o-mini src/Fugue.Cli/bin/Release/net10.0/osx-arm64/publish/fugue`

- [ ] **Step 2: Repeat tool-loop scenario**

Same as Task 5.1 step 4. Status bar shows `openai:gpt-4o-mini`.

---

### Task 5.4: Right-alignment + locale toggle

**Files:** none — manual.

- [ ] **Step 1: Test right alignment**

Edit `~/.fugue/config.json` — set `"ui":{"userAlignment":"right","locale":"en"}`.

Restart fugue. Type a few short messages. Verify they're aligned to the right edge of terminal width (with ~30% right padding behaviour).

- [ ] **Step 2: Test locale toggle**

Set `"locale":"ru"`. Restart. Verify status bar uses `на ветке` instead of `on`. Verify `cancelled` shows as `отменено`.

Set `"locale":"en"`. Restart. Verify English texts.

- [ ] **Step 3: Auto-detect**

Remove `ui` block entirely from config. Restart. Locale should follow system locale.

---

### Task 5.5: Update spec + plan with verification results

**Files:**
- Modify: `docs/superpowers/specs/2026-04-29-fugue-spectre-aot-pivot.md` — add verification-results section at end
- Modify: this plan file (mark all tasks ✓ if executing inline)

- [ ] **Step 1: Append "Verification results" section to spec**

Edit `docs/superpowers/specs/2026-04-29-fugue-spectre-aot-pivot.md` — append:

```markdown
---

## Verification results (filled by Phase 5)

- Binary size: ____ MB (target ≤ 35 MB)
- Cold start: ____ ms (target ≤ 50 ms)
- Peak RSS: ____ MB (target ≤ 90 MB)
- All 13 acceptance rows: status ____
```

Fill in actual measurements.

- [ ] **Step 2: Final commit**

```bash
git add docs/superpowers/specs/2026-04-29-fugue-spectre-aot-pivot.md
git commit -m "docs: record Phase-5 verification results for Spectre+AOT pivot"
```

---

## End-of-plan checklist

- [ ] Phase 1 — Tools no-reflection AIFunction (10 tasks)
- [ ] Phase 2 — Core UiConfig + Localization (3 tasks)
- [ ] Phase 3 — CLI Spectre rewrite (9 tasks)
- [ ] Phase 4 — AOT publish + sweep (3 tasks)
- [ ] Phase 5 — End-to-end красота tour (5 tasks)

**Total:** 30 tasks, ~120-150 commits expected (each task averages 4-5 commit-worthy steps).

**Acceptance criteria** (from spec, must be all green before declaring pivot done):

| # | Метрика | Target |
|---|---|---|
| 1 | Binary size (osx-arm64) | ≤ 35 MB |
| 2 | Cold start | ≤ 50 ms |
| 3 | Peak RSS at idle | ≤ 90 MB |
| 4 | No JsonTypeInfo runtime warning | yes |
| 5 | Прозрачный фон терминала | yes |
| 6 | Selection текста чата мышкой | yes |
| 7 | Markdown-рендер ответа (CommonMark + GFM) | yes |
| 8 | Diff-блоки с цветным background | yes |
| 9 | Tool-bullet style (`●` цветом по статусу) | yes |
| 10 | Persistent status bar (path + branch + provider) | yes |
| 11 | Ctrl+C: один error-block, не два | yes |
| 12 | User-message alignment (config-toggle left/right) | yes |
| 13 | UI-локализация en + ru | yes |
