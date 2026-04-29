# Fugue — pivot на Spectre.Console + Native AOT

> **🚫 ABANDONED — 2026-04-29.**
> Pivot не запущен. Причина: исследование показало, что Spectre.Console **by design** не поддерживает
> persistent input bar (зафиксированный ввод внизу экрана пока контент стримится сверху) —
> это подтверждено мейнтейнерами в [discussion #1233](https://github.com/spectreconsole/spectre.console/discussions/1233)
> и в документации `LiveDisplay` («not thread safe, not supported with prompts»).
> REPL-модель (`user>` / `assistant>` строками в scrollback) для пользователя оказалась неприемлемой —
> хотел Claude-Code-style UX. Остаёмся на Terminal.Gui v2 + R2R+Trim.
>
> Спека сохранена для истории решения. Если в будущем вернёмся к выбору TUI:
> кандидаты под full AOT + persistent input — **Termina** (Aaronontheweb/termina, AOT-compatible,
> reactive, активно развивается, но C#-only API), **Consolonia** (XAML), **Hex1b** (React-like).
> Terminal.Gui v2 свой AOT-путь так и не довёл до конца (DeepCloner на ConfigurationManager).

---

**Дата:** 2026-04-29
**Заменяет (частично):** `docs/superpowers/specs/2026-04-29-fugue-design.md` — секции "TUI / MVU layer" и "AOT-риски и митигации".
**Статус MVP до pivot'а:** работает на Terminal.Gui v2 + R2R+Trim, 42 MB single-file бинарь, два UX-блокера (фокус, selection), один runtime-warning ("Reflection-based serialization disabled"), AOT не достигнут.

---

## Цели

1. **Красота**: рендер ответа ассистента как CommonMark с fenced code blocks; tool-вызовы — отдельные панели со статус-иконками; пользовательский ввод — отдельная стилизованная зона; selection текста мышкой работает (стоковый терминальный scrollback).
2. **Native AOT всерьёз**: `PublishAot=true`, бинарь ≤ 35 MB, cold start ≤ 50 ms, peak RSS на idle ≤ 90 MB. Никаких `JsonTypeInfo`-warning'ов в runtime.
3. **Сохранить F#-only-codebase, multi-provider, MAF-агента, AgentSession, streaming, шесть тулзов.**

## Не-цели

- Полноценный split-layout с persistent input bar (это противоречит REPL-модели Spectre).
- Custom F#-конфиг через `.fsx` в runtime — несовместимо с AOT, отложено в v0.3 (см. ниже «Future work»).
- Markdown-syntax-highlighting **внутри** code-блоков — `Spectre.Console.Markdown` подсвечивает basic-tokens, full-language coloring (ColorCode/Markdig+Roslyn) — вне scope.
- История стрелками вверх/вниз в ReadLine — отложена в v0.2.
- Permission-prompt перед mutating tools — отложен в v0.2 (как было решено в исходной спеке).

## Architecture overview

Solution `Fugue.slnx`, четыре проекта. После pivot'а:

```
src/
  Fugue.Core/      ← без изменений
  Fugue.Agent/     ← без изменений
  Fugue.Tools/     ← rewrite ToolRegistry + новые AIFunctions/*
  Fugue.Cli/       ← rewrite целиком (Mvu/Model/Update/View → Repl/Render/ReadLine)
tests/
  Fugue.Tests/     ← удаляем UpdateTests, добавляем ToolRegistryTests + ReadLineTests
```

### Что выживает без изменений

| Файл | Роль |
|---|---|
| `Fugue.Core/Domain.fs` | `Role`, `ContentBlock`, `Message`, `Session`, `ProviderConfig`, `AppConfig` |
| `Fugue.Core/Config.fs` | загрузка `~/.fugue/config.json` + env |
| `Fugue.Core/SystemPrompt.fs` | базовый system-prompt |
| `Fugue.Agent/AgentFactory.fs` | `IChatClient` → `AIAgent` фабрика на 3 провайдера |
| `Fugue.Agent/Conversation.fs` | `taskSeq`-стрим из `RunStreamingAsync`, парсинг `TextContent`/`FunctionCallContent`/`FunctionResultContent` |
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
| `Fugue.Cli/View.fs` | delete | Заменяется `Render.fs`. |
| `Fugue.Cli/Program.fs` | rewrite (тоньше) | Bootstrap + Ctrl+C handler + `Repl.run`. |
| `Fugue.Cli/Fugue.Cli.fsproj` | modify | Drop `Terminal.Gui`, add `Spectre.Console` + `Spectre.Console.Markdown`. `<PublishAot>true</PublishAot>`. |
| `Fugue.Cli/Repl.fs` | new | Главный async loop. |
| `Fugue.Cli/Render.fs` | new | Pure renderers, возвращают `IRenderable`. |
| `Fugue.Cli/ReadLine.fs` | new | Свой multi-line ReadKey-loop. |
| `tests/Fugue.Tests/UpdateTests.fs` | delete | Update'а нет. |
| `tests/Fugue.Tests/ToolRegistryTests.fs` | new | Schema validity + arg parsing для каждой из 6 тулзов. |
| `tests/Fugue.Tests/ReadLineTests.fs` | new | Pure key-handler unit-тесты (без `Console`-IO). |

### Зависимости (NuGet)

**Удаляем:** `Terminal.Gui` (2.0.1)

**Добавляем:**
- `Spectre.Console` (`0.49.*`) — REPL-рендер, `LiveDisplay`, `Panel`, `Markup`, `Rule`, `Rows`.
- `Spectre.Console.Markdown` (`0.49.*`) — CommonMark-рендерер для финальной отрисовки ответа ассистента.

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

/// Fetch a required string argument. Throws ArgumentException if missing or wrong kind.
val getStr     : AIFunctionArguments -> string -> string

/// Fetch optional string. Returns None if missing or null.
val tryGetStr  : AIFunctionArguments -> string -> string option

/// Fetch optional int. Returns None if missing or not numeric.
val tryGetInt  : AIFunctionArguments -> string -> int option

/// Fetch optional bool. Returns None if missing.
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

### Файловая раскладка `Fugue.Tools/`

```
src/Fugue.Tools/
  ReadTool.fs
  WriteTool.fs
  EditTool.fs
  BashTool.fs
  GlobTool.fs
  GrepTool.fs
  AiFunctions/
    DelegatedFn.fs   ← общий wrapper-класс
    Args.fs          ← парсинг AIFunctionArguments
    ReadFn.fs
    WriteFn.fs
    EditFn.fs
    BashFn.fs
    GlobFn.fs
    GrepFn.fs
  ToolRegistry.fs
```

`Fugue.Tools.fsproj` `Compile Include` order: implementations → `AiFunctions/DelegatedFn.fs` → `AiFunctions/Args.fs` → `AiFunctions/*Fn.fs` → `ToolRegistry.fs`.

### Тесты — `ToolRegistryTests.fs`

Для каждой из 6 тулзов:
1. `buildAll cwd` возвращает список длины 6, имена совпадают с `names`.
2. JSON-схема каждого `AIFunction.JsonSchema` валидна (`JsonDocument.Parse` не падает), есть `"type":"object"`, есть `"properties"`, есть `"required"`.
3. `InvokeAsync` с правильно собранными args возвращает строку — happy-path запуск против tmp-папки.
4. `InvokeAsync` с пропущенным required-полем кидает `ArgumentException`.
5. `InvokeAsync` с заполненным optional-полем правильно парсится (например, `Read` с `offset=2, limit=3`).

---

## REPL + streaming render

### `Spectre.Console.Markdown` disclaimer

Spectre.Console **не имеет** встроенного CommonMark-рендерера в основном пакете; есть собственный markup-язык (`[bold]hi[/]`). CommonMark предоставляет community-пакет `Spectre.Console.Markdown` (Patrik Svensson, AOT-friendly).

**План:** используем `Spectre.Console.Markdown` для финальной отрисовки ответа ассистента. **Если** при AOT-publish'е он даёт неустранимые trim warnings — fallback на свой минимальный рендер (только fenced code → `Panel`, inline `**bold**`/`*italic*`/`` `code` `` через regex → Spectre `Markup`). Decision-point — этап AOT-sweep'а в плане.

### `Repl.fs` — главный loop

```fsharp
let run (agent: AIAgent) (cwd: string) : Task<unit> = task {
    let session = agent.GetNewSession()
    use cancelSrc = new CancelSource()  // wraps Console.CancelKeyPress

    while not cancelSrc.QuitRequested do
        match! ReadLine.readAsync (Render.prompt cwd) cancelSrc.Token with
        | None ->                                  // Ctrl+D на пустом → quit
            cancelSrc.RequestQuit()
        | Some "" -> ()                            // пустой Enter → no-op
        | Some s when s = "/exit" || s = "/quit" ->
            cancelSrc.RequestQuit()
        | Some s when s = "/clear" ->
            AnsiConsole.Clear()
            session.Reset()
        | Some userInput ->
            Render.userMessage userInput |> AnsiConsole.Write
            do! streamAndRender agent session userInput cancelSrc
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
    let tools = Dictionary<string, ToolState>()  // id → state

    let layout () : IRenderable =
        let parts = ResizeArray<IRenderable>()
        parts.Add(Render.assistantLive (assistantBuf.ToString()))
        for KeyValue(_, t) in tools do
            parts.Add(Render.toolPanel t)
        Rows(parts) :> IRenderable

    let live = AnsiConsole.Live(layout()).AutoClear(true)

    try
        do! live.StartAsync(fun ctx -> task {
            for upd in Conversation.run agent session input streamCts.Token do
                match upd with
                | TextChunk t           -> assistantBuf.Append t |> ignore
                | ToolStarted(id,n,a)   -> tools.[id] <- Running(n,a)
                | ToolCompleted(id,o,err) ->
                    let prev = tools.[id]
                    tools.[id] <- finishState prev err o
                ctx.UpdateTarget(layout())
                ctx.Refresh()
        })
        // Live cleared (AutoClear). Render final state with markdown.
        Render.assistantFinal (assistantBuf.ToString()) |> AnsiConsole.Write
        for KeyValue(_, t) in tools do
            Render.toolPanel t |> AnsiConsole.Write
    with
    | :? OperationCanceledException ->
        Render.cancelled |> AnsiConsole.Write
    | ex ->
        Render.errorBlock ex.Message |> AnsiConsole.Write
}
```

`ToolState`:
```fsharp
type ToolState =
    | Running of name: string * args: string
    | Completed of name: string * args: string * output: string
    | Failed of name: string * args: string * err: string
```

### `Render.fs` — pure renderers

```fsharp
module Fugue.Cli.Render

open Spectre.Console
open Spectre.Console.Rendering

val prompt          : cwd: string -> string                  // строка для ReadLine
val userMessage     : string -> IRenderable                  // "[grey]you[/]" + indented text
val assistantLive   : string -> IRenderable                  // Panel header="assistant", plain text
val assistantFinal  : string -> IRenderable                  // Panel + Markdown(text)
val toolPanel       : ToolState -> IRenderable               // ⟳ / ✓ / ✗ panel
val cancelled       : IRenderable                            // "[yellow]⚠ cancelled[/]"
val errorBlock      : string -> IRenderable                  // [red]error[/] panel
```

Tool panel design:

| State | Header | Border | Output |
|---|---|---|---|
| `Running` | `[yellow]⟳ {name}[/]` | `Rounded` (yellow) | `[dim]args:[/] {args}\n[dim]running…[/]` |
| `Completed` | `[green]✓ {name}[/]` | `Rounded` (green) | `[dim]args:[/] {args}\n{output (truncated to 12 lines or 800 chars)}` |
| `Failed` | `[red]✗ {name}[/]` | `Heavy` (red) | `[dim]args:[/] {args}\n[red]{err}[/]` |

### Cancel UX

В `Program.fs`:
- `Console.CancelKeyPress += handler` — единожды при старте.
- На входе в REPL: `Console.TreatControlCAsInput <- true` (Ctrl+C приходит в `ReadLine.readAsync` как keystroke).
- Перед стартом стрима: `Console.TreatControlCAsInput <- false` (Ctrl+C идёт через `CancelKeyPress` → `streamCts.Cancel()`).
- После стрима: обратно `<- true`.

`CancelSource`:
```fsharp
type CancelSource() =
    let mutable streamCts: CancellationTokenSource option = None
    let mutable quit = false
    member _.QuitRequested = quit
    member _.RequestQuit() = quit <- true
    member _.NewStreamCts() = ...
    member _.OnCtrlC(args: ConsoleCancelEventArgs) =
        match streamCts with
        | Some cts -> cts.Cancel(); args.Cancel <- true
        | None     -> quit <- true; args.Cancel <- true
    interface IDisposable with member _.Dispose() = ...
```

Двойной Ctrl+C во время стрима: первый отменяет стрим, второй — снова приходит как `CancelKeyPress`, но `streamCts` уже `None` → ставится `quit = true`, REPL выходит после возврата к prompt.

---

## ReadLine — multi-line, Shift+Enter, cancel

### API

```fsharp
module Fugue.Cli.ReadLine

/// Read a (possibly multi-line) line from the console.
/// Returns None on Ctrl+D at empty buffer (= quit signal).
/// Returns Some "" on Enter at empty buffer.
/// Cancels via the supplied token (e.g. when CancelSource flips it).
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

### Internal state + redraw

```fsharp
type private S = {
    mutable Buffer:        ResizeArray<char>
    mutable Cursor:        int                 // flat index в Buffer
    mutable LinesRendered: int                 // сколько terminal-строк мы написали в прошлом redraw
    PromptText:            string              // напр. "[grey]> [/]"
    PromptVisLen:          int                 // visible-длина без ANSI
    Width:                 int                 // Console.WindowWidth на момент старта
}
```

`redraw st`:
1. Если `LinesRendered > 0` — поднять курсор: эмит `\x1b[{LinesRendered-1}A\r\x1b[J`.
2. Вывести prompt + buffer (с реальными `\n`).
3. Пересчитать `LinesRendered` = `1 + count('\n' in buffer) + Σ overflow при превышении ширины`.
4. Курсор в позицию `st.Cursor`: вычислить (row, col), эмитить `\x1b[{Δrow}A\x1b[{col}G`.

**Overflow на длинном single-line input** (`buffer.Length + PromptVisLen > Width`) принят как cosmetic glitch в MVP. Long-line soft-wrap — v0.2.

### Pure key-handler для тестов

Вместо моков `Console` выносим decision-логику в чистую функцию:

```fsharp
type Action =
    | Continue        // изменили state, redraw
    | Submit of string
    | Quit
    | Wipe            // wipe buffer, redraw

val applyKey : ConsoleKeyInfo -> S -> Action
```

`ReadLineTests.fs` гоняет `applyKey` против собранного руками `S`-стейта. Тест-кейсы:
- insert printable → state.Buffer / Cursor правильно меняются
- Backspace на границе строки (после `\n`)
- ↑↓ snap к колонке: `"hello\nhi"` с курсором в col=4 на первой строке, ↓ → курсор в col=2 (длина "hi") на второй
- Shift+Enter в середине строки: split в правильном месте
- Ctrl+D на пустом буфере → `Quit`
- Ctrl+D на непустом → delete-char
- Ctrl+C → `Wipe`
- Enter → `Submit (string buffer)`

Сам `redraw` (terminal IO) — без юнит-тестов, проверяется в ручном smoke.

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
    <Compile Include="Render.fs" />
    <Compile Include="Repl.fs" />
    <Compile Include="Program.fs" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Spectre.Console" Version="0.49.*" />
    <PackageReference Include="Spectre.Console.Markdown" Version="0.49.*" />
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
| `Spectre.Console.Markdown` | заявляет AOT-clean — может пройти без warnings | если не пройдёт → fallback на свой минимальный markdown-рендер |
| `FSharp.Core` | IL2104 (известно из прошлой сборки) | `<TrimmerRootAssembly Include="FSharp.Core" />` |

**Sweep-процедура** (отдельная фаза в плане):
1. `dotnet publish src/Fugue.Cli -c Release -r osx-arm64` (warnings, но НЕ как errors на этой фазе).
2. Собрать список warnings, классифицировать по 4 категориям (наш код / MEAI / Anthropic / Spectre).
3. По каждой категории — точечный fix или `TrimmerRootAssembly`.
4. Включить `TreatWarningsAsErrors=true` обратно, repeat publish — должен пройти чисто.

### Multi-provider AOT-матрица

| Провайдер | Зависимость | Smoke-сценарий |
|---|---|---|
| Ollama | `OllamaSharp` 5.x | `qwen3.6:27b`, локально |
| Anthropic | `Anthropic.SDK` 3.x + `Microsoft.Agents.AI.Anthropic` | `claude-opus-4-7`, через `ANTHROPIC_API_KEY` |
| OpenAI | `OpenAI` 2.x + `Microsoft.Extensions.AI.OpenAI` | `gpt-4o-mini`, через `OPENAI_API_KEY` |

Все три провайдера должны пройти end-to-end tool-loop (см. ниже) — иначе мульти-провайдер заявленный, но не проверенный.

---

## Verification

### Phase 1 — `Fugue.Tools` (custom AIFunction)

1. `dotnet test tests/Fugue.Tests --filter ToolRegistryTests` — все 6 schemas валидны, parse аргументов работает на happy-path и на missing/extra полях.
2. `dotnet test tests/Fugue.Tests` — все остальные тулз-тесты (Read/Write/Edit/Bash/Glob/Grep) зелёные.

### Phase 2 — `Fugue.Cli` (Spectre + ReadLine, Debug build)

1. `dotnet build src/Fugue.Cli -c Debug` — собирается без warning'ов.
2. `dotnet run --project src/Fugue.Cli` — REPL поднимается, prompt отображается.
3. Ручной ReadLine smoke:
   - печатаем `hello` → видно;
   - Backspace, ←, →, Home, End — работают;
   - Shift+Enter → перенос строки в рамках буфера; Enter → submit;
   - Ctrl+C на непустом буфере → wipe + fresh prompt;
   - Ctrl+D на пустом буфере → выход.
4. `ReadLineTests.fs` — `applyKey`-юниты зелёные.

### Phase 3 — AOT publish

1. `dotnet publish src/Fugue.Cli -c Release -r osx-arm64 /p:PublishAot=true` — сборка без warning'ов в нашем коде, suppressed-warning'ов из пакетов с явным `Justification`.
2. `ls -lh fugue` — бинарь ≤ 35 MB.
3. `/usr/bin/time -l ./fugue --version` — startup ≤ 50 ms, peak RSS ≤ 90 MB. (`--version` флаг — добавить.)
4. `./fugue` — REPL поднимается без runtime exception'ов.

### Phase 4 — End-to-end tool loop (3 провайдера)

Сценарии, прогоняемые против каждого из 3 провайдеров (Ollama → Anthropic → OpenAI):

1. «Прочитай README.md и сделай саммари» → live-стрим `⟳ Read(README.md)` → `✓ Read(...)` → markdown-ответ с саммари. Selection текста ответа мышкой работает.
2. «Создай файл TEST.md со словом hi» → файл на диске, ассистент подтверждает.
3. Длинный prompt (например, «объясни весь алгоритм A* с примерами кода»), Ctrl+C посередине → `[yellow]⚠ cancelled[/]` → возврат к prompt, следующий ввод работает.
4. `/exit` → нормальный выход, без `OperationCanceledException` в stderr.

### Acceptance criteria

| Метрика | Цель | Текущее (R2R+Trim) |
|---|---|---|
| Binary size (osx-arm64) | ≤ 35 MB | 42 MB |
| Cold start | ≤ 50 ms | ~150 ms |
| Peak RSS at idle | ≤ 90 MB | ~120 MB |
| `JsonTypeInfo` warning при первом сообщении | отсутствует | присутствует |
| Selection текста чата мышкой | работает | не работает (TG) |
| Word-wrap длинных строк | работает | работает (после фикса) |
| Markdown-рендер ответа | работает (CommonMark) | нет |
| Ctrl+C: wipe input / cancel stream | работает | частично |

Pivot считаем успешным, когда все 8 строк зелёные.

---

## Future work (вне scope MVP)

- **v0.2:** permission-prompt перед `Bash`/`Write`/`Edit`; история стрелками в ReadLine; soft-wrap длинных строк; slash-команды `/help`, `/cost`, `/model`; markdown syntax highlighting в code-блоках (через ColorCode/Markdig).
- **v0.3:** F#-конфиг через `~/.fugue/fugue.fsx`. Решение между двумя паттернами:
  1. **xmonad-style «recompile self».** `fugue` сравнивает хэш `fugue.fsx` с известным; если изменился — `dotnet publish` темплейта с инжектнутым `Compile Include="fugue.fsx"`, exec нового AOT-бинаря. Требует `.NET SDK` у пользователя, первая компиляция ~30-60 сек.
  2. **Sidecar JIT-процесс.** Главный AOT-`fugue` для горячего пути + отдельный `fugue-script` (JIT, ~150 ms) для `fugue.fsx`. Общаются через stdio JSON-RPC. Sidecar регистрирует кастомные tools/system prompts.

  Предварительный favourite — (1) (минимум сложности в runtime), но decision откладывается до v0.3.
- **v0.4:** MCP-клиент (через MAF), persistent-сессии в `~/.fugue/sessions/`, diff-вьюер для Edit.
