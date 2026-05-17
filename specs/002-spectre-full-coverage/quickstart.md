# Quickstart — Phase 2 Adapter Surface

**Audience**: a Fugue contributor porting one or more rendering or
interactive call-sites from direct `Spectre.Console` use onto the new
Phase 2 adapter surface (US1–US5 / P1–P5).

**Coverage target**: this doc maps the call patterns each user-story
unblocks, with "before" (current direct-Spectre code from
`src/Fugue.Cli/`) and "after" (adapter call) snippets. P2–P5 use
hypothetical port snippets — those call-sites either don't exist yet
in the current codebase or are stubbed.

> **Status**: Phase 1 design (`002-spectre-full-coverage`).
> Implementation pending — Phase 2 will land as five PRs (one per
> user story), starting with **P1** (it sets the bridge pattern that
> P2 reuses internally).

---

## 0. Setup

The adapter project's `.fsproj` gains one new PackageReference for the
JSON widget (data-model.md §0.2):

```xml
<ItemGroup>
  <PackageReference Include="Spectre.Console"      Version="0.49.*" />
  <PackageReference Include="Spectre.Console.Json" Version="0.49.*" />  <!-- NEW in Phase 2 P2 -->
</ItemGroup>
```

Add the project reference from your call-site once per
`Fugue.Cli` / `Fugue.Surface` source file cluster you plan to port:

```xml
<ItemGroup>
  <ProjectReference Include="..\Fugue.Adapters.Console\Fugue.Adapters.Console.fsproj" />
</ItemGroup>
```

Open the namespace at the top of the file being ported:

```fsharp
open Fugue.Adapters.Console
```

Then **remove** the existing Spectre opens *file by file*, only after
the last Spectre call-site in that file is ported. Each Spectre call
that remains keeps the existing `open Spectre.Console` until the last
site is migrated. After the file is fully ported, the file MUST NOT
contain `open Spectre.Console` (SC-002).

---

## 1. P1 — Custom-renderable bridge

The bridge unblocks **every** remaining REPL port: any call-site that
returns an `IRenderable` (whether hand-rolled or from a helper like
`MarkdownRender.toRenderable`) can wrap that value into the adapter
without rewriting its construction.

### Real call-site: `src/Fugue.Cli/Render.fs` — `assistantFinal`

#### Before

```fsharp
// src/Fugue.Cli/Render.fs (excerpt — current Phase 1 code)
let assistantFinal (text: string) : IRenderable =
    if colorEnabled then
        let inner = MarkdownRender.toRenderable text   // returns IRenderable
        if bubblesMode then
            let panel = Panel(inner)
            panel.Border <- BoxBorder.Rounded
            panel.BorderStyle <- Style.Parse (themeColor "dim" "dim #1e3a5a")
            panel.Header <- PanelHeader(" Claude ")
            panel :> IRenderable
        else inner
    else Text(text) :> _
```

This returns an `IRenderable` directly — there is no clean way to embed
it into a `Composition.Panel` in Phase 1 because `Composition.Panel`
takes a `Composition`, not an `IRenderable`.

#### After (using P1 bridge)

```fsharp
// src/Fugue.Cli/Render.fs (excerpt — Phase 2 P1 port)
open Fugue.Adapters.Console

let assistantFinalComposition (text: string) : Composition =
    if colorEnabled then
        // MarkdownRender.toRenderable still returns IRenderable —
        // we don't rewrite the markdown pipeline, we just bridge it.
        let inner =
            MarkdownRender.toRenderable text
            |> Renderable.fromSpectre        // single intentional leak
            |> Composition.ofRenderable      // embed in the typed tree

        if bubblesMode then
            // Now the panel is a typed Composition — no IRenderable in sight.
            Composition.panel
                Border.Rounded
                (Some (SafeText.ofLiteral " Claude "))
                inner
        else
            inner
    else
        Composition.Leaf (
            Primitive.Styled (Style.empty, SafeText.ofUser text))

// Renderer.toRawAnsi + Surface.batch driver unchanged from §11 of
// the Phase 1 quickstart.
let assistantFinal (ctx: RenderContext) (text: string) =
    match Renderer.toRawAnsi ctx (assistantFinalComposition text) with
    | Ok ansi -> Surface.batch (DrawOp.RawAnsi ansi)
    | Error e -> eprintfn $"render error: %A{e}"
```

The `MarkdownRender.toRenderable` function still uses Spectre internally
— that's fine; we're not porting it in this PR. The bridge lets the
*consumer* (Render.fs) speak adapter, while the *producer* keeps its
existing implementation. Subsequent PRs can port MarkdownRender on its
own.

#### Exception safety

If a user-supplied renderable throws during render, the adapter catches
at the boundary and returns `Error (RenderFailed (typeName, message))`
(FR-009). No exception leaks across the adapter boundary:

```fsharp
type ThrowingRenderable() =
    interface IRenderable with
        member _.Measure(_, _) = Measurement(1, 1)
        member _.Render(_, _) = failwith "boom"

let comp =
    ThrowingRenderable() :> IRenderable
    |> Renderable.fromSpectre
    |> Composition.ofRenderable

match Renderer.toRawAnsi ctx comp with
| Error (RenderFailed (name, msg)) ->
    // name = "Fugue.Cli.Render.ThrowingRenderable"
    // msg  = "boom"
    eprintfn $"renderable {name} failed: {msg}"
| _ -> ()
```

---

## 2. P2 — Data-display primitives

Eight new widgets: `Tree`, `Json`, `BarChart`, `BreakdownChart`,
`Calendar`, `FigletText`, `Grid`, `TextPath`. These are Phase 2's
broadest single class of additions.

> **Note**: most P2 widgets don't have existing call-sites today;
> snippets below are hypothetical "future REPL port" examples for
> features Phase 3 will likely build (file-tree picker, JSON
> tool-result viewer, model-throughput bar chart, etc.).

### 2.1 `Tree` — file hierarchy picker

#### Before (hypothetical — would call Spectre directly)

```fsharp
let tree = Tree("/")
let src = TreeNode("src")
src.AddNode("Cli") |> ignore
src.AddNode("Adapters.Console") |> ignore
tree.AddNode(src) |> ignore
AnsiConsole.Write tree
```

#### After

```fsharp
open Fugue.Adapters.Console

let root =
    TreeNode.branch (SafeText.ofLiteral "/") [
        TreeNode.branch (SafeText.ofLiteral "src") [
            TreeNode.leaf (SafeText.ofLiteral "Cli")
            TreeNode.leaf (SafeText.ofLiteral "Adapters.Console")
        ]
    ]

match Tree.create root with
| Ok tree ->
    let comp = Composition.ofTree tree
    Renderer.toRawAnsi ctx comp |> ignore
| Error e ->
    eprintfn $"tree build failed: %A{e}"
```

### 2.2 `Json` — tool-result payload viewer

```fsharp
open Fugue.Adapters.Console

let renderToolResult (payload: string) : Result<Composition, RenderError> =
    Json.create payload
    |> Result.map Composition.ofJson

// Usage in a tool dispatch handler:
match renderToolResult toolOutput with
| Ok comp ->
    match Renderer.toRawAnsi ctx comp with
    | Ok ansi -> Surface.batch (DrawOp.RawAnsi ansi)
    | Error _ -> ()
| Error (InvalidArgument (_, msg)) ->
    // payload was not valid JSON; render as plain text instead
    let fallback =
        Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofUser toolOutput))
    Renderer.toRawAnsi ctx fallback |> ignore
| Error _ -> ()
```

### 2.3 `BarChart` — model throughput dashboard

```fsharp
open Fugue.Adapters.Console

let throughputChart (rows: (string * float) list) : Result<Composition, RenderError> =
    result {
        let! items =
            rows
            |> List.map (fun (label, value) ->
                BarChartItem.create
                    (SafeText.ofUser label)
                    value
                    (Colour.Theme "accent"))
            |> List.sequenceResultM
        let! chart =
            BarChart.create
                (Some (SafeText.ofLiteral "tokens/sec"))
                items
                60
        return Composition.ofBarChart chart
    }
```

### 2.4 `Grid` — ragged-row rejection (spec edge case 1.3)

```fsharp
open Fugue.Adapters.Console

let cols = [
    GridColumn.create (1, 1, 0, 0) Alignment.LeftAlign
    GridColumn.create (1, 1, 0, 0) Alignment.RightAlign
]

let rows : Composition list list = [
    [ Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "a"))
      Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "1")) ]
    // RAGGED — only one column!
    [ Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "b")) ]
]

match Grid.create cols rows with
| Ok _ -> ()
| Error (InvalidArgument ("Grid", detail)) ->
    // detail = "ragged rows: expected 2 columns, got 1"
    eprintfn $"grid build failed: {detail}"
| Error _ -> ()
```

---

## 3. P3 — Live, Status, Progress

Callback-scoped sessions for streaming UX. Per data-model.md §4, the
session handle lives only inside the `body` callback.

### 3.1 `Status` — spinner during LLM "thinking"

#### Before (hypothetical)

```fsharp
AnsiConsole.Status().StartAsync("Thinking…", fun ctx -> task {
    let! response = LLM.thinkAsync prompt
    ctx.Status <- "Done"
    return response
}) |> ignore
```

#### After

```fsharp
open Fugue.Adapters.Console

let console = Console.default_ ()

async {
    use cts = new System.Threading.CancellationTokenSource ()
    let! result =
        Status.run
            console
            Spinner.Dots
            (SafeText.ofLiteral "Thinking…")
            cts.Token
            (fun session -> async {
                let! response = LLM.thinkAsync prompt |> Async.AwaitTask
                let! _ =
                    StatusSession.updateMessage session
                        (SafeText.ofLiteral "Done") |> async.Return
                return Ok response
            })
    match result with
    | Ok response       -> handleResponse response
    | Error (UserCancelled _) -> printfn "cancelled"
    | Error e           -> eprintfn $"status failed: %A{e}"
}
```

### 3.2 `Live` — token streaming

```fsharp
open Fugue.Adapters.Console

let streamResponse
    (console: Console)
    (tokens: System.Collections.Generic.IAsyncEnumerable<string>)
    (cancellation: System.Threading.CancellationToken)
    : Async<Result<string, RenderError>> =

    Live.run
        console
        (Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "")))
        cancellation
        (fun session -> async {
            let buffer = System.Text.StringBuilder ()
            let enumerator = tokens.GetAsyncEnumerator (cancellation)
            try
                let mutable keepGoing = true
                while keepGoing do
                    let! hasNext = enumerator.MoveNextAsync().AsTask() |> Async.AwaitTask
                    if hasNext then
                        buffer.Append enumerator.Current |> ignore
                        let comp =
                            Composition.Leaf (
                                Primitive.Styled (Style.empty, SafeText.ofUser (string buffer)))
                        match LiveSession.update session comp with
                        | Ok () -> ()
                        | Error e -> raise (System.Exception (sprintf "%A" e))
                    else
                        keepGoing <- false
            finally
                do enumerator.DisposeAsync().AsTask().Wait()
            return Ok (string buffer)
        })
```

### 3.3 `Progress` — multi-task progress for parallel tool calls

```fsharp
open Fugue.Adapters.Console

Progress.run console cts.Token (fun session -> async {
    let! tasks =
        [ "Reading file 1"; "Reading file 2"; "Reading file 3" ]
        |> List.map (fun desc ->
            ProgressSession.addTask session (SafeText.ofUser desc))
        |> List.sequenceResultM
        |> async.Return

    match tasks with
    | Ok ts ->
        // Drive each task in parallel; increment as work completes.
        do! ts |> List.map (fun t -> async {
            for _ in 1 .. 10 do
                do! Async.Sleep 100
                let _ = ProgressTask.increment t 10.0
                ()
            let _ = ProgressTask.complete t
            ()
        }) |> Async.Parallel |> Async.Ignore
        return Ok ()
    | Error e -> return Error e
})
```

### 3.4 Concurrent-session rejection

```fsharp
open Fugue.Adapters.Console

async {
    let console = Console.default_ ()
    let cts = new System.Threading.CancellationTokenSource ()

    let outer = Live.run console (Composition.stack []) cts.Token (fun _ -> async {
        // Trying to start another Live on the same console:
        let! inner = Live.run console (Composition.stack []) cts.Token (fun _ ->
            async { return Ok () })
        match inner with
        | Error (ConcurrentLiveSession _) -> ()   // expected
        | _ -> failwith "should have been rejected"
        return Ok ()
    })
    ()
}
```

---

## 4. P4 — Interactive prompts

### 4.1 `SelectionPrompt` — model picker

#### Before (real call-site, simplified — Repl.fs `/model set`)

```fsharp
let prompt =
    SelectionPrompt<string>()
        .Title("Pick a model")
        .AddChoices [| "claude-opus-4-7"; "claude-sonnet-4-7"; "gpt-5o" |]
let picked = AnsiConsole.Prompt prompt
```

#### After

```fsharp
open Fugue.Adapters.Console

async {
    let console = Console.default_ ()
    let cts = new System.Threading.CancellationTokenSource ()
    let choices : (string * Composition) list = [
        "claude-opus-4-7",
            Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "claude-opus-4-7"))
        "claude-sonnet-4-7",
            Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "claude-sonnet-4-7"))
        "gpt-5o",
            Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "gpt-5o"))
    ]

    let title =
        Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "Pick a model"))

    let! result =
        SelectionPrompt.ask console title choices cts.Token

    match result with
    | Ok modelId               -> applyModel modelId
    | Error (UserCancelled _)  -> printfn "cancelled"
    | Error (NoInteractiveConsole _) ->
        eprintfn "cannot prompt in non-interactive environment"
    | Error e                  -> eprintfn $"prompt failed: %A{e}"
}
```

### 4.2 `TextPrompt` with typed validator

```fsharp
open Fugue.Adapters.Console

let parsePort : Validator<int> = fun input ->
    match System.Int32.TryParse input with
    | true, n when n > 0 && n <= 65535 -> Ok n
    | true, n   -> Error $"port {n} out of range (1..65535)"
    | false, _  -> Error "must be an integer"

async {
    let console = Console.default_ ()
    let cts = new System.Threading.CancellationTokenSource ()
    let! port =
        TextPrompt.ask
            console
            (Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "Listen on port: ")))
            parsePort
            cts.Token

    match port with
    | Ok p                    -> bindServer p
    | Error (UserCancelled _) -> ()
    | Error e                 -> eprintfn $"prompt failed: %A{e}"
}
```

Spectre's native re-prompt loop runs while `parsePort` returns `Error`,
displaying the error message inline; the wrapper only returns once the
user provides input that the validator accepts.

### 4.3 `ConfirmationPrompt` — approval-mode confirmation

```fsharp
open Fugue.Adapters.Console

async {
    let console = Console.default_ ()
    let cts = new System.Threading.CancellationTokenSource ()
    let! approved =
        ConfirmationPrompt.ask
            console
            (Composition.Leaf (Primitive.Styled (
                Style.empty |> Style.withFg (Colour.Theme "warning"),
                SafeText.ofUser $"Run '{cmd}'?")))
            false                   // default: deny
            cts.Token
    match approved with
    | Ok true                 -> runCmd cmd
    | Ok false                -> printfn "denied"
    | Error (UserCancelled _) -> printfn "cancelled"
    | Error e                 -> eprintfn $"prompt failed: %A{e}"
}
```

---

## 5. P5 — Console infrastructure & testing

### 5.1 Production write — replaces direct `AnsiConsole.Write`

#### Before (real — `src/Fugue.Cli/Surface.fs` mailbox)

```fsharp
let spectre (action: IAnsiConsole -> unit) =
    let console = AnsiConsole.Console
    action console
```

#### After

```fsharp
open Fugue.Adapters.Console

let console = Console.default_ ()

let write (comp: Composition) =
    match Console.write console comp with
    | Ok ()   -> ()
    | Error e -> eprintfn $"write failed: %A{e}"
```

### 5.2 Test console — deterministic capture

```fsharp
open Fugue.Adapters.Console

[<Fact>]
let ``panel renders with rounded border at width 40`` () =
    let test = Console.test 40 true
    let comp =
        Composition.panel
            Border.Rounded
            (Some (SafeText.ofLiteral " hello "))
            (Composition.Leaf (Primitive.Styled (Style.empty, SafeText.ofLiteral "body")))

    match Console.write test comp with
    | Ok () ->
        match Console.capture test comp with
        | Ok captured ->
            Assert.Contains("hello", captured)
            Assert.Contains("body",  captured)
            Assert.Contains("╭",     captured)   // rounded-border corner
        | Error _ -> Assert.Fail "capture failed"
    | Error _ -> Assert.Fail "write failed"
```

### 5.3 Exception rendering — replaces `WriteException`

#### Before

```fsharp
try doWork ()
with ex ->
    AnsiConsole.WriteException(ex,
        ExceptionFormats.ShortenTypes ||| ExceptionFormats.ShortenMethods)
```

#### After

```fsharp
open Fugue.Adapters.Console

try doWork ()
with ex ->
    let console = Console.default_ ()
    let _ = Exceptions.renderWith console ExceptionFormat.compact ex
    ()
```

### 5.4 Cursor inspection in tests

```fsharp
open Fugue.Adapters.Console

[<Fact>]
let ``status session hides cursor while active`` () =
    let test = Console.test 80 true
    // ... drive Status.run against `test` ...
    // (Spectre's Status.Start hides the cursor for the spinner)
    match Console.cursorState test with
    | Hidden               -> ()                       // expected
    | Visible (r, c)       -> Assert.Fail $"cursor visible at ({r}, {c})"
    | UnknownState         -> Assert.Fail "cursor state unknown on test console"
```

---

## 6. Migration order — recommended sequence

When porting an existing REPL file from direct Spectre to the
adapter, follow this sequence (each step is a smaller, reviewable PR):

1. **Wire the bridge.** Add `open Fugue.Adapters.Console` next to the
   existing `open Spectre.Console`. Replace each `IRenderable`-typed
   value with `Renderable.fromSpectre value |> Composition.ofRenderable`.
   The file still uses Spectre at the producer level; the consumer
   speaks adapter. The build and tests stay green. *Granularity: 1
   file per PR.*
2. **Enrich with typed primitives.** For each construction site of a
   Phase 1 primitive (`Markup`, `Text`, `Panel`, `Padder`, `Rows`,
   `Align`, `Table`, `Rule`), replace with the equivalent
   `Composition.*` smart constructor. The `Renderable.fromSpectre`
   bridges shrink to only what genuinely cannot be expressed yet.
3. **Replace data-display widgets with P2 primitives.** Where the
   file calls Spectre's `Tree`, `Json`, `BarChart`, etc. directly,
   swap for `DataDisplays.*` wrappers. Each swap usually shortens
   the code (no more `:> IRenderable` casts, no more mutation chains).
4. **Promote Live UX to P3 sessions.** If the file calls
   `AnsiConsole.Live` / `Status` / `Progress`, port to the
   callback-scoped `Live.run` / `Status.run` / `Progress.run`. This
   is the largest single mechanical change per call-site — the
   `async` body structure shifts noticeably. Do this AFTER P1+P2 are
   stable so the inner composition is already a `Composition`.
5. **Promote prompts to P4.** Same shape as P3: callback-scoped,
   `Async<Result<_,_>>`-returning. Validators get typed.
6. **Replace direct console access with P5.** Final step: any
   remaining `AnsiConsole.Write`, `AnsiConsole.WriteException`, or
   `IAnsiConsole` parameter becomes `Console.write`,
   `Exceptions.render`, or `Console` parameter respectively. After
   this step, `rg "Spectre\.Console" <file>` returns zero matches
   (SC-002 satisfied for that file).
7. **Remove `open Spectre.Console`.** Last step in the file. CI
   guard catches accidental reintroduction in future PRs.

For test files, the migration is shorter — Phase 1 tests do not need
migration (SC-005). New tests written in Phase 2 SHOULD use
`Console.test` from P5; existing tests stay on their current
`Renderer.toRawAnsi`-based pattern.

---

## Appendix A — Port log (filled by port PRs, not by adapter Phase 2 PRs)

| Date | Source file | Sites ported | Bridges remaining | PR |
|------|-------------|-------------:|------------------:|----|
| _(empty until first Phase 3 port lands)_ | | | | |

**Coverage baseline (Phase 2, 2026-05-16)**: The five user-story PRs
ship the adapter surface; downstream REPL porting is a separate wave
tracked here. Each entry above is a row added by a port PR after Phase
2 lands.

---

*End of quickstart.md. Cross-references: see
[data-model.md](./data-model.md) for the entity-by-entity contract,
[research.md](./research.md) for the five design-decision rationales,
[coverage-matrix.md](./coverage-matrix.md) for the per-Spectre-type
status table.*
