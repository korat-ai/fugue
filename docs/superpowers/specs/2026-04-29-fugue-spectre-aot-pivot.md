# Fugue — pivot на Spectre.Console + Native AOT

> **Статус: ACTIVE** (разморожена 2026-04-29 после анализа Claude-Code-скриншотов).
>
> История: спека была написана 2026-04-29, помечена ABANDONED тем же днём из-за
> ошибочного предположения, что нужен «persistent input bar в стиле Claude Code».
> При повторном анализе скриншотов оказалось, что Claude Code сам — это
> **scrollback + rich rendering + persistent статус-бар внизу**, а не TUI с
> зафиксированным input'ом. Это **именно та архитектура**, которая идеально ложится
> на Spectre.Console. Pivot возобновлён.

**Дата:** 2026-04-29
**Заменяет (частично):** `docs/superpowers/specs/2026-04-29-fugue-design.md` — секции «TUI / MVU layer» и «AOT-риски и митигации».
**Статус MVP до pivot'а:** работает на Terminal.Gui v2 + R2R+Trim, 42 MB single-file бинарь, два UX-блокера (фокус, selection — частично починены), один runtime-warning («Reflection-based serialization disabled» — починен), AOT не достигнут, _фон не прозрачный, нет markdown, нет diff-рендера, выглядит как «приложение в коробке»_.

---

## Цели

1. **Visual parity с Claude Code** (по reference-скриншотам в Linear): scrollback-flow, прозрачный фон терминала, markdown-рендер ответов, tool-вызовы как `● Bash(...)` буллеты, diff-блоки с цветовыми префиксами, persistent статус-бар на двух нижних строках. Никаких полноэкранных рамок.
2. **Native AOT всерьёз**: `PublishAot=true`, бинарь ≤ 35 MB, cold start ≤ 50 ms, peak RSS на idle ≤ 90 MB. Никаких runtime-warning'ов.
3. **Сохранить F#-only-codebase, multi-provider, MAF-агента, AgentSession, streaming, шесть тулзов, файловый логгер** (`~/.fugue/log.txt`).

## Не-цели

- **Syntax highlighting** в code blocks. Будет dim-моноширинный текст с line numbers слева. Hi-light — отдельная итерация в v0.2.
- Полный split-layout TUI с pinned input bar при работающем стриме — Claude Code сам так не делает; не нужно.
- Custom F#-конфиг через `.fsx` в runtime — несовместимо с AOT, отложено в v0.3.
- История стрелками вверх/вниз в ReadLine — отложена в v0.2.
- Permission-prompt перед mutating tools — отложен в v0.2.
- Token counter `ctx:32%` в статус-баре — отложен в v0.2 (нужно подключать tiktoken-аналог).
- `bypass permissions on (shift+tab to cycle)` в статус-баре — отложен в v0.2 вместе с permission-prompt'ом.

## Architecture overview

Solution `Fugue.slnx`, четыре проекта. После pivot'а:

```
src/
  Fugue.Core/      ← без изменений (Domain, Config, SystemPrompt, Log)
  Fugue.Agent/     ← без изменений (AgentFactory, Conversation, Discovery)
  Fugue.Tools/     ← rewrite ToolRegistry + новые AiFunctions/*
  Fugue.Cli/       ← rewrite целиком (Mvu/Model/Update/View → новые модули)
tests/
  Fugue.Tests/     ← удаляем UpdateTests, добавляем ToolRegistryTests + ReadLineTests + RenderTests
```

### Что выживает без изменений

| Файл | Роль |
|---|---|
| `Fugue.Core/Log.fs` | append-only file logger в `~/.fugue/log.txt` |
| `Fugue.Core/Domain.fs` | `Role`, `ContentBlock`, `Message`, `Session`, `ProviderConfig`, `AppConfig` |
| `Fugue.Core/Config.fs` | resolution: explicit env > file > implicit env |
| `Fugue.Core/SystemPrompt.fs` | базовый system-prompt |
| `Fugue.Agent/AgentFactory.fs` | `IChatClient` → `AIAgent` фабрика на 3 провайдера |
| `Fugue.Agent/Conversation.fs` | `taskSeq`-стрим, propagates OCE на cancel |
| `Fugue.Agent/Discovery.fs` | env-key + `~/.claude/settings.json` + Ollama-локал |
| `Fugue.Tools/{Read,Write,Edit,Bash,Glob,Grep}Tool.fs` | реализации тулзов остаются F#-идиоматичными с `option`-типами |

### Что переписывается

| Файл | Действие | Причина |
|---|---|---|
| `Fugue.Tools/ToolRegistry.fs` | rewrite | `AIFunctionFactory.Create` использует reflection на F# `option`. Переходим на кастомный `AIFunction`-наследник без reflection. |
| `Fugue.Tools/AiFunctions/*` | new | Один общий `DelegatedAIFunction`-класс + одна `create cwd`-функция на тулзу. |
| `Fugue.Cli/Mvu.fs` | delete | REPL не требует MVU. |
| `Fugue.Cli/Model.fs` | delete | State держим как локальные переменные REPL-loop'а. |
| `Fugue.Cli/Update.fs` | delete | Без MVU нет reducer'а. |
| `Fugue.Cli/View.fs` | delete | Заменяется `Render.fs` + специализированными модулями. |
| `Fugue.Cli/Program.fs` | rewrite (тоньше) | Bootstrap + Ctrl+C handler + `Repl.run`. |
| `Fugue.Cli/Fugue.Cli.fsproj` | modify | Drop `Terminal.Gui`, add `Spectre.Console` + `Markdig`. `<PublishAot>true</PublishAot>`. |
| `Fugue.Cli/Repl.fs` | new | Главный async loop. |
| `Fugue.Cli/Render.fs` | new | Pure renderers, возвращают `IRenderable`. Обвязка над специализированными модулями. |
| `Fugue.Cli/MarkdownRender.fs` | new | Markdig AST → Spectre `IRenderable` визитор. |
| `Fugue.Cli/DiffRender.fs` | new | Heuristic detection unified-diff в tool output → цветной рендер. |
| `Fugue.Cli/StatusBar.fs` | new | Persistent 2-line bottom bar через ANSI absolute positioning. |
| `Fugue.Cli/ReadLine.fs` | new | Свой multi-line ReadKey-loop. |
| `tests/Fugue.Tests/UpdateTests.fs` | delete | Update'а нет. |
| `tests/Fugue.Tests/ToolRegistryTests.fs` | new | Schema validity + arg parsing для каждой из 6 тулзов. |
| `tests/Fugue.Tests/ReadLineTests.fs` | new | Pure key-handler unit-тесты. |
| `tests/Fugue.Tests/RenderTests.fs` | new | Markdown + diff-detect, snapshot-style сравнение текстового output'а. |

### Зависимости (NuGet)

**Удаляем:** `Terminal.Gui` (2.0.1)

**Добавляем:**
- `Spectre.Console` (`0.49.*`) — REPL-рендер, `LiveDisplay`, `Panel`, `Markup`, `Rule`, `Rows`, `Tree`.
- `Markdig` (`0.41.*`) — AOT-friendly CommonMark + GFM парсер. Используется как AST-источник для нашего собственного визитора.

**Не трогаем:** `Microsoft.Agents.AI`, `Microsoft.Extensions.AI`, `Microsoft.Agents.AI.Anthropic`, `Anthropic.SDK`, `OllamaSharp`, OpenAI client.

---

## Tool registration — кастомный AIFunction (no-reflection)

### Базовый класс — `Fugue.Tools.AiFunctions.DelegatedFn`

```fsharp
module Fugue.Tools.AiFunctions.DelegatedFn

open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.AI

/// Single concrete AIFunction subclass parametrised by name/desc/schema/body.
/// All six tools instantiate this with different bodies — no subclass per tool.
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

### Helpers — `Fugue.Tools.AiFunctions.Args`

```fsharp
module Fugue.Tools.AiFunctions.Args

open System.Text.Json
open Microsoft.Extensions.AI

val getStr     : AIFunctionArguments -> string -> string
val tryGetStr  : AIFunctionArguments -> string -> string option
val tryGetInt  : AIFunctionArguments -> string -> int option
val tryGetBool : AIFunctionArguments -> string -> bool option
```

`AIFunctionArguments` — это `IReadOnlyDictionary<string, object?>`. Значения приходят как `JsonElement` (когда LLM вернул JSON-args) или примитивы. Helpers покрывают оба варианта.

### Фабрика на тулзу — пример `ReadFn`

```fsharp
// src/Fugue.Tools/AiFunctions/ReadFn.fs
module Fugue.Tools.AiFunctions.ReadFn

open Microsoft.Extensions.AI
open Fugue.Tools.AiFunctions

let private schema = DelegatedFn.parseSchema """{
  "type":"object",
  "properties":{
    "path":  {"type":"string","description":"Absolute or cwd-relative file path"},
    "offset":{"type":"integer","description":"1-based line to start reading"},
    "limit": {"type":"integer","description":"Max lines to read"}
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
            return ReadTool.read cwd path offset limit
        }) :> AIFunction
```

Аналогично для `WriteFn`, `EditFn`, `BashFn`, `GlobFn`, `GrepFn`. Каждая ~12-25 строк.

### `ToolRegistry.fs`

```fsharp
module Fugue.Tools.ToolRegistry

open Microsoft.Extensions.AI
open Fugue.Tools.AiFunctions

let buildAll (cwd: string) : AIFunction list = [
    ReadFn.create  cwd
    WriteFn.create cwd
    EditFn.create  cwd
    BashFn.create  cwd
    GlobFn.create  cwd
    GrepFn.create  cwd
]

let names = [ "Read"; "Write"; "Edit"; "Bash"; "Glob"; "Grep" ]
```

### Тесты — `ToolRegistryTests.fs`

Для каждой из 6 тулзов:
1. `buildAll cwd` возвращает список длины 6, имена совпадают с `names`.
2. JSON-схема каждого `AIFunction.JsonSchema` валидна (`JsonDocument.Parse` не падает), есть `"type":"object"`, есть `"properties"`, есть `"required"`.
3. `InvokeAsync` с правильно собранными args возвращает строку — happy-path запуск против tmp-папки.
4. `InvokeAsync` с пропущенным required-полем кидает `ArgumentException`.
5. `InvokeAsync` с заполненным optional-полем правильно парсится (например, `Read` с `offset=2, limit=3`).

---

## Rich rendering — tool-bullets, markdown, diff, code blocks

Цель — визуальный паритет с Claude Code-скриншотами:

- ассистент-текст течёт без рамок и без `assistant>` префикса, рендерится как markdown
- tool-вызовы — `● Name(args)` буллетом (цвет по статусу), output индентом под буллетом
- code blocks — серым моноширинным текстом + line numbers слева, **без syntax highlighting в MVP**
- diff-блоки — авто-детекция unified-diff в tool output, подсветка `-` красным фоном, `+` зелёным, `@@` пурпурным
- inline `code` — бирюзовый текст без рамки
- bold/italic/headers/lists — стандартный CommonMark через Markdig
- прозрачный фон терминала сохраняется (Spectre сам по себе background не льёт)

### `Fugue.Cli.Render` — интерфейс

```fsharp
module Fugue.Cli.Render

open Spectre.Console
open Spectre.Console.Rendering

/// User input, как один render-вызов: `[grey]›[/] {text}` без рамок.
val userMessage    : string -> IRenderable

/// Финальный markdown-рендер ответа ассистента (после стрима).
/// Конвертит markdown через MarkdownRender.toRenderable.
val assistantFinal : string -> IRenderable

/// Live-state ассистента ВО ВРЕМЯ стрима: plain-text Spectre `Text` (markdown
/// ещё не парсится — буфер растёт по chunks).  По окончании стрима поверх
/// печатается assistantFinal.
val assistantLive  : string -> IRenderable

/// Tool-call в любом из 3 состояний.
val toolBullet     : ToolState -> IRenderable

/// Error block: красный `⚠ {msg}` без рамки.
val errorLine      : string -> IRenderable

/// Маркер cancel'а.
val cancelled      : IRenderable
```

`ToolState`:
```fsharp
type ToolState =
    | Running   of name: string * args: string
    | Completed of name: string * args: string * output: string
    | Failed    of name: string * args: string * err: string
```

### Tool bullet — конкретные стили

| Состояние | Bullet | Header | Body |
|---|---|---|---|
| `Running`   | `[yellow]●[/]` | `[bold]{name}[/]([dim]{args}[/])` | `[dim]running…[/]` под буллетом, отступ 2 пробела |
| `Completed` | `[green]●[/]`  | `[bold]{name}[/]([dim]{args}[/])` | output авто-detect: либо diff-render (см. ниже), либо markdown, либо просто dim моноширинный текст. Truncate >= 12 строк / 800 chars c суффиксом `[dim]+ {N} more lines[/]` |
| `Failed`    | `[red]●[/]`    | `[bold]{name}[/]([dim]{args}[/])` | `[red]{err}[/]` под буллетом, отступ 2 пробела |

Реализация — комбинация Spectre `Markup` для самой буллет-строки + `Padder(content, padding=Padding(2,0,0,0))` для тела.

### Markdown — `MarkdownRender.fs`

Парсим текст через `Markdig.Parsers.MarkdownPipeline` (минимальный pipeline — CommonMark + GFM tables + GFM strikethrough), потом обходим AST и возвращаем Spectre `IRenderable`.

```fsharp
module Fugue.Cli.MarkdownRender

open Markdig
open Markdig.Syntax
open Markdig.Syntax.Inlines
open Spectre.Console
open Spectre.Console.Rendering

/// Pre-built pipeline (создаётся однажды на старте процесса).
val pipeline : MarkdownPipeline

/// Convert markdown source to a single composite Spectre IRenderable.
val toRenderable : string -> IRenderable
```

Маппинг блочных узлов:

| Markdig узел | Spectre output |
|---|---|
| `ParagraphBlock` | `Markup` с inline-рендером |
| `HeadingBlock` (h1-h6) | `Markup` с уровнем выделения: h1 — `[bold underline]`, h2 — `[bold]`, h3+ — `[bold dim]` |
| `FencedCodeBlock` / `CodeBlock` | `Padder(Rows(line-pairs))` где каждая line-pair = `Columns([Markup($"[dim]{lineNo:3}[/]"); Text(lineContent, dim style)])`, отступ 2 слева |
| `ListBlock` | `Markup` с префиксом `• ` для unordered, `1. ` для ordered |
| `QuoteBlock` | `Padder(child)` с префиксом `[dim]│[/] ` на каждой строке |
| `Table` (GFM) | Spectre `Table`, без рамок, header — `[bold]` |
| `ThematicBreakBlock` (`---`) | Spectre `Rule(string.Empty)` |

Маппинг inline-узлов (внутри `Markup`-строки):

| Markdig inline | Spectre markup |
|---|---|
| `LiteralInline` | escape'нутый текст |
| `EmphasisInline` (`*x*`) | `[italic]{x}[/]` |
| `EmphasisInline` (`**x**`) | `[bold]{x}[/]` |
| `EmphasisInline` (`***x***`) | `[bold italic]{x}[/]` |
| `CodeInline` (`` `x` ``) | `[teal]{x}[/]` (без рамки, как на скриншотах Claude Code) |
| `LinkInline` `[text](url)` | `[link={url}]{text}[/]` (Spectre native) |
| `LineBreakInline` | `\n` |
| `HtmlInline` | escape'нутый текст |

Все user-supplied строки прогоняются через `Markup.Escape` перед вставкой в markup-фрагмент — это критично, иначе квадратные скобки в чужом тексте парсятся как Spectre markup.

### Diff-блоки — `DiffRender.fs`

Эвристика: если строка output'а из tool-call'а содержит **минимум 2 unified-diff-маркера** в первых 50 строках (`@@ ... @@`, или последовательность `-...` / `+...`), вызываем diff-render. Иначе — markdown.

```fsharp
module Fugue.Cli.DiffRender

/// Detect whether the input looks like a unified diff.
val looksLikeDiff : string -> bool

/// Render unified-diff text. Lines are styled by their first character:
///   '-' → red bg, dim red fg
///   '+' → green bg, dim green fg
///   '@' → magenta dim
///   ' ' → plain dim
/// Returns a Spectre `Rows(...)` of styled `Markup` lines.
val toRenderable : string -> IRenderable
```

Без сложного парсера — просто line-by-line scan. ~80 LOC.

### Финальный сценарий рендера в Repl

При `ToolCompleted(id, output, isError=false)`:

```fsharp
let body =
    if DiffRender.looksLikeDiff output then DiffRender.toRenderable output
    elif looksLikeMarkdown output      then MarkdownRender.toRenderable output
    else                                    Render.dimMonospace output
```

`looksLikeMarkdown` — простая эвристика на наличие fenced code blocks или header-маркеров. Если ни то ни другое — выводим как dim plain текст.

---

## REPL + streaming

### `src/Fugue.Cli/Repl.fs` — главный loop

```fsharp
let run (agent: AIAgent) (cwd: string) : Task<unit> = task {
    let session = agent.GetNewSession()
    use cancelSrc = new CancelSource()
    StatusBar.start cwd  // запускает фоновую перерисовку нижних 2 строк

    while not cancelSrc.QuitRequested do
        match! ReadLine.readAsync (Render.prompt cwd) cancelSrc.Token with
        | None ->
            cancelSrc.RequestQuit()
        | Some "" -> ()
        | Some s when s = "/exit" || s = "/quit" ->
            cancelSrc.RequestQuit()
        | Some s when s = "/clear" ->
            AnsiConsole.Clear()
            session.Reset()
        | Some userInput ->
            Render.userMessage userInput |> AnsiConsole.Write
            do! streamAndRender agent session userInput cancelSrc

    StatusBar.stop ()
}
```

Slash-команды MVP: `/exit`, `/quit`, `/clear`. `/help`, `/cost`, `/model` — v0.2.

### `streamAndRender`

```fsharp
let private streamAndRender
        (agent: AIAgent) (session: AgentSession) (input: string) (cancelSrc: CancelSource)
        : Task<unit> = task {
    use streamCts = cancelSrc.NewStreamCts()
    let assistantBuf = StringBuilder()
    let tools = Dictionary<string, ToolState>()

    let layout () : IRenderable =
        let parts = ResizeArray<IRenderable>()
        if assistantBuf.Length > 0 then
            parts.Add(Render.assistantLive (assistantBuf.ToString()))
        for KeyValue(_, t) in tools do
            parts.Add(Render.toolBullet t)
        Rows(parts) :> IRenderable

    let live = AnsiConsole.Live(layout()).AutoClear(true)

    try
        do! live.StartAsync(fun ctx -> task {
            for upd in Conversation.run agent session input streamCts.Token do
                match upd with
                | TextChunk t              -> assistantBuf.Append t |> ignore
                | ToolStarted(id,n,a)      -> tools.[id] <- Running(n,a)
                | ToolCompleted(id,o,err)  ->
                    let prev = tools.[id]
                    tools.[id] <- finishState prev err o
                ctx.UpdateTarget(layout())
                ctx.Refresh()
        })
        // Live cleared (AutoClear). Render final state with markdown.
        if assistantBuf.Length > 0 then
            Render.assistantFinal (assistantBuf.ToString()) |> AnsiConsole.Write
        for KeyValue(_, t) in tools do
            Render.toolBullet t |> AnsiConsole.Write
    with
    | :? OperationCanceledException ->
        Render.cancelled |> AnsiConsole.Write
    | ex ->
        Render.errorLine ex.Message |> AnsiConsole.Write
}
```

Ключевое отличие от первой версии спеки — **нет `Panel` с header'ом `assistant`**. Ассистент-текст выводится «голым», как на скриншотах Claude Code.

---

## Persistent status bar — `StatusBar.fs`

Две нижних строки терминала зарезервированы под status bar. Содержимое в MVP:

```
~/path/to/cwd on git-branch
fugue · ollama:qwen3.6:27b
```

Серый dim текст. Цветные акценты на провайдере (`ollama` — синий, `anthropic` — оранжевый, `openai` — зелёный).

### API

```fsharp
module Fugue.Cli.StatusBar

/// Initialise: query terminal size, register SIGWINCH handler, draw initial bar.
val start : cwd: string -> unit

/// Update fields (called on git branch change, /clear, etc).
val refresh : unit -> unit

/// Restore terminal to normal mode (remove reservation, clear bar lines).
val stop : unit -> unit
```

### Реализация

При `start`:
1. `Console.WriteLine("\n\n")` — резервирует 2 пустых строки внизу.
2. Сохраняем `terminalHeight = Console.WindowHeight`.
3. Setup ANSI scroll region: `\x1b[1;{height-2}r` — теперь scrollback ограничен верхней частью, нижние 2 строки — наша зона.
4. Draw bar (см. ниже).
5. Возвращаем cursor в scroll-area: `\x1b[{height-2};1H`.

При `refresh` (вызывается при изменении содержимого):
1. Save cursor: `\x1b[s`.
2. Move to bar position: `\x1b[{height-1};1H`.
3. Clear lines: `\x1b[2K\n\x1b[2K`.
4. Render content в эти строки.
5. Restore cursor: `\x1b[u`.

Git branch — через `Process.Start("git", "branch --show-current")`, кэшируем на 2 секунды чтобы не дёргать на каждый refresh.

При `stop`:
1. Reset scroll region: `\x1b[r`.
2. Move to absolute bottom: `\x1b[{height};1H`.
3. Clear last 2 lines.

### SIGWINCH (resize)

Регистрируем `PosixSignalRegistration.Create(PosixSignal.SIGWINCH, handler)`:
- handler перечитывает `Console.WindowHeight`/`WindowWidth`
- пересоздаёт scroll region
- вызывает `refresh`

Под Windows — через `Console.CancelKeyPress`-аналог? Поскольку первичная цель — macOS/Linux, Windows ставим как «works but not pretty» в v0.2.

### Тесты

Поскольку `StatusBar` пишет сырые ANSI escape-sequences в `Console.Out`, юнит-тесты — через подменённый `IAnsiConsole`-stream. Проверяем:
1. `start` эмитит правильный set-scroll-region escape для текущей высоты терминала.
2. `refresh` эмитит save-cursor + move + clear + restore в правильном порядке.
3. `stop` сбрасывает scroll region.

---

## ReadLine — multi-line, Shift+Enter, cancel

(Без изменений relative к первой версии спеки.)

### API

```fsharp
module Fugue.Cli.ReadLine

val readAsync :
    prompt: string ->
    ct: CancellationToken ->
    Task<string option>
```

### Key bindings

| Key | Action |
|---|---|
| `printable char` | insert at cursor |
| `Enter` | submit (return `Some buf`) |
| `Shift+Enter` | insert `\n` |
| `Backspace` | delete char left of cursor |
| `Delete` | delete char at cursor |
| `← →` | cursor move (across `\n`) |
| `↑ ↓` | line move (snap to column) |
| `Home` / `End` | start / end of current visual line |
| `Ctrl+A` / `Ctrl+E` | start / end of buffer |
| `Ctrl+U` | clear current line |
| `Ctrl+K` | delete from cursor to end of line |
| `Ctrl+C` | wipe buffer, fresh prompt (не выходим) |
| `Ctrl+D` (буфер пуст) | return `None` → REPL выходит |
| `Ctrl+D` (буфер не пуст) | delete-char (как bash) |

История стрелками — вне MVP.

### Pure key-handler для тестов

```fsharp
type Action =
    | Continue
    | Submit of string
    | Quit
    | Wipe

val applyKey : ConsoleKeyInfo -> S -> Action
```

`ReadLineTests.fs` гоняет `applyKey` против собранного руками `S`-стейта.

### Внешний look prompt'а

Render via Spectre: `[grey]›[/] ` префикс. Никаких рамок вокруг (на скриншотах Claude Code тонкая рамка ЕСТЬ, но это побочный эффект Ink — для нас лишний bytecount). Если хочется — добавим в v0.2 как опцию.

---

## AOT — config + trim warnings + verification

### `Fugue.Cli.fsproj` финал

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>Fugue.Cli</RootNamespace>
    <AssemblyName>fugue</AssemblyName>

    <PublishAot>true</PublishAot>
    <IsAotCompatible>true</IsAotCompatible>
    <InvariantGlobalization>true</InvariantGlobalization>
    <StripSymbols>true</StripSymbols>
    <OptimizationPreference>Size</OptimizationPreference>
    <IlcGenerateStackTraceData>true</IlcGenerateStackTraceData>

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

`Directory.Build.props` дополнить `<IsAotCompatible>true</IsAotCompatible>` — это включит trim/AOT-warning'и на all proj-ах, поможет выловить reflection-патерны до publish'а.

### Trim/AOT warnings — стратегия

**Принцип:** `TreatWarningsAsErrors=true` остаётся. `<NoWarn>` для IL2026/IL3050/IL3053/IL2104 в нашем коде запрещены.

Если warning из стороннего пакета:
- сначала пытаемся точечный fix через `[<DynamicDependency>]` или `[<UnconditionalSuppressMessage(..., Justification = "<reason>: <upstream-issue-link>")>]` — каждый suppress с явным комментарием почему;
- если без reflection пакет работать не может — `<TrimmerRootAssembly Include="<Pkg>" />` (~+1-3 MB к бинарнику), документируем причину рядом.

**Ожидаемые источники предупреждений:**

| Источник | Симптом | План |
|---|---|---|
| `Anthropic.SDK` | reflection в JSON-сериализаторе моделей | `JsonSerializerIsReflectionEnabledByDefault=true`; точечный `DynamicDependency` если останется warning |
| `Microsoft.Agents.AI` (1.3-prerelease) | `AgentSession.GetMessagesAsync` reflection-copy | то же самое |
| `Markdig` | заявляет AOT-clean — должен пройти без warnings | если не пройдёт → fallback на наш минимальный CommonMark-парсер (только paragraphs + fenced code + bold/italic/inline) |
| `Spectre.Console` | mostly AOT-clean | точечные `DynamicDependency` если что-то всплывёт |
| `FSharp.Core` | IL2104 (известно из прошлой сборки) | `<TrimmerRootAssembly Include="FSharp.Core" />` |

**Sweep-процедура** (отдельная фаза в плане):
1. `dotnet publish src/Fugue.Cli -c Release -r osx-arm64` (warnings, но НЕ как errors на этой фазе).
2. Собрать список warnings, классифицировать по 5 категориям.
3. По каждой категории — точечный fix или `TrimmerRootAssembly`.
4. Включить `TreatWarningsAsErrors=true` обратно, repeat publish — должен пройти чисто.

### Multi-provider AOT-матрица

| Провайдер | Зависимость | Smoke-сценарий |
|---|---|---|
| Ollama | `OllamaSharp` 5.x | `qwen3.6:27b`, локально |
| Anthropic | `Anthropic.SDK` 3.x + `Microsoft.Agents.AI.Anthropic` | `claude-opus-4-7`, через `ANTHROPIC_API_KEY` |
| OpenAI | `OpenAI` 2.x + `Microsoft.Extensions.AI.OpenAI` | `gpt-4o-mini`, через `OPENAI_API_KEY` |

Все три провайдера должны пройти end-to-end tool-loop (см. ниже).

---

## Verification

### Phase 1 — `Fugue.Tools` (custom AIFunction)

1. `dotnet test tests/Fugue.Tests --filter ToolRegistryTests` — все 6 schemas валидны, parse аргументов работает.
2. `dotnet test tests/Fugue.Tests` — все остальные тулз-тесты зелёные.

### Phase 2 — `Fugue.Cli` (Spectre + ReadLine + Render, Debug build)

1. `dotnet build src/Fugue.Cli -c Debug` — собирается без warning'ов.
2. `dotnet run --project src/Fugue.Cli` — REPL поднимается.
3. Ручной ReadLine smoke (см. выше).
4. `ReadLineTests.fs`, `RenderTests.fs` — зелёные.

### Phase 3 — AOT publish

1. `dotnet publish src/Fugue.Cli -c Release -r osx-arm64 /p:PublishAot=true` — без warning'ов в нашем коде, suppressed-warning'ы из пакетов с явным `Justification`.
2. `ls -lh fugue` — бинарь ≤ 35 MB.
3. `/usr/bin/time -l ./fugue --version` — startup ≤ 50 ms, peak RSS ≤ 90 MB.
4. `./fugue` — REPL поднимается без runtime exception'ов.

### Phase 4 — End-to-end красота-tour (3 провайдера)

Сценарии против каждого из Ollama → Anthropic → OpenAI:

1. **Markdown.** «Объясни в markdown что такое А\*-алгоритм с примером кода на F#» → видим header, bullet list, fenced code block с моноширинным F#-кодом и line numbers, **прозрачный фон сохранился**.
2. **Tool bullet.** «Прочитай README.md и сделай саммари» → `[yellow]●[/] Read(README.md)` → переходит в `[green]●[/] Read(...)` → output индентом → markdown-саммари.
3. **Diff.** «Сделай rename переменной X в файле Y» → MAF возвращает diff в tool output → видим красные `-` строки, зелёные `+` строки, пурпурные `@@`.
4. **Cancel.** Длинный prompt, Ctrl+C посередине → `[yellow]⚠ cancelled[/]` (одна строка, не две!), возврат к prompt'у. Status bar внизу не дёрнулся.
5. **Status bar.** При запуске видим внизу `~/Korat_Agent on main\nfugue · ollama:qwen3.6:27b`. После `cd ../somewhere && fugue` — путь обновился. После переключения git-ветки руками — branch обновляется в течение 2 секунд (через poll).
6. **Resize.** `cmd+plus` / `cmd+minus` в iTerm — status bar остаётся на 2 нижних строках, не дублируется и не теряется.
7. **Selection.** Выделить текст ответа ассистента мышкой → копируется чисто, без ANSI escape'ов в clipboard. Cmd+F поиск работает.
8. `/exit` → нормальный выход, status bar убран, scroll region восстановлен.

### Acceptance criteria

| Метрика | Цель | Текущее (R2R+Trim+TG) |
|---|---|---|
| Binary size (osx-arm64) | ≤ 35 MB | 42 MB |
| Cold start | ≤ 50 ms | ~150 ms |
| Peak RSS at idle | ≤ 90 MB | ~120 MB |
| `JsonTypeInfo` warning при первом сообщении | отсутствует | присутствует |
| Прозрачный фон терминала | работает | заливка TG-схемой |
| Selection текста чата мышкой | работает (стоковый scrollback) | не работает |
| Markdown-рендер ответа (CommonMark + GFM) | работает | нет |
| Diff-блоки с цветным background | работают | нет |
| Tool-bullet style (`●` цветом по статусу) | работает | FrameView с заголовком |
| Persistent status bar (path + branch + provider) | работает | нет |
| Ctrl+C: один error-block, не два | работает | работает (после fix'а в hot-fix-ветке) |

Pivot считаем успешным, когда все 11 строк зелёные.

---

## Future work (вне scope MVP)

- **v0.2:** permission-prompt перед `Bash`/`Write`/`Edit`; история стрелками в ReadLine; soft-wrap длинных строк; slash-команды `/help`, `/cost`, `/model`; **syntax highlighting в code blocks** (regex-токенайзер на 6 языков, ~250 LOC); token counter для status bar (`ctx:32%`); `bypass permissions on (shift+tab to cycle)` — индикатор и переключатель режима permission.
- **v0.3:** F#-конфиг через `~/.fugue/fugue.fsx`. Решение между двумя паттернами:
  1. **xmonad-style «recompile self».** `fugue` сравнивает хэш `fugue.fsx` с известным; если изменился — `dotnet publish` темплейта с инжектнутым `Compile Include="fugue.fsx"`, exec нового AOT-бинаря. Требует `.NET SDK` у пользователя, первая компиляция ~30-60 сек.
  2. **Sidecar JIT-процесс.** Главный AOT-`fugue` для горячего пути + отдельный `fugue-script` (JIT, ~150 ms) для `fugue.fsx`. Общаются через stdio JSON-RPC.

  Предварительный favourite — (1).
- **v0.4:** MCP-клиент (через MAF), persistent-сессии в `~/.fugue/sessions/`, file-tree side-panel при larger output (опциональный TG-режим? — отдельно обсуждается).
