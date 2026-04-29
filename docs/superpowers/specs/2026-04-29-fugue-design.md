# Fugue — Design

> _A fugue in F# for your terminal._
> _Many voices, one console._

**Date:** 2026-04-29
**Status:** approved (brainstorming complete, ready for implementation plan)
**Folder:** `/Users/roman/Documents/dev/Korat_Agent` _(папка диска не переименована — путь оставляем как есть, имя продукта = Fugue)_
**Binary:** `fugue`

## Context

Существующие CLI-агенты (Claude Code, Codex CLI, Aider) построены на Node.js или Python — старт ~300–700 мс, RSS 150–300 МБ. Цель — собрать минималистичный CLI-агент с тем же workflow, но на стеке, дающем быстрый старт и низкое потребление памяти: **F# + Microsoft Agent Framework + Terminal.Gui.Elmish + .NET 10 Native AOT**.

**Имя продукта — Fugue.** Выбрано в brainstorming-итерации: фуга — музыкальная форма, в которой несколько голосов переплетаются по строгим правилам, что прямо метафорирует мульти-агентность с предсказуемым поведением; пасхалка про F# (Bach писал прелюдии и фуги во всех тональностях, F#-минор — одна из легендарных); этимология от лат. _fugere_ «бежать» намекает на скорость; жёсткая структура формы — на стабильность.

Это greenfield-проект (директория пустая). Ниже — финальный дизайн после трёх итераций уточнений.

## Goals (MVP)

- TUI-чат: streaming-ответ модели, история в одной сессии, отмена по Esc.
- 6 базовых tools: `Read`, `Write`, `Edit`, `Bash`, `Glob`, `Grep`.
- Мульти-провайдер: Anthropic / OpenAI / Ollama, выбирается через конфиг.
- Native AOT single-file binary < 60 МБ, RSS на старте < 80 МБ.
- Discovery-flow: при отсутствии конфига — читаем env, `~/.claude/settings.json`, пингуем Ollama, предлагаем выбор.

## Non-goals (deferred)

- **v0.2:** permission-prompt перед `Bash`/`Write`/`Edit`, `fugue init`-визард, расширенный discovery (Codex, Aider, Copilot, LM Studio), OAuth-токен из `~/.claude/.credentials.json`.
- **v0.3:** slash-команды (`/help`, `/clear`, `/model`, `/cost`), загрузка `CLAUDE.md`, persistent-сессии в `~/.fugue/sessions/`.
- **v0.4:** MCP-клиент, markdown-renderer, diff-вьюер для `Edit`.

## Architecture

### Solution layout

```
Fugue.slnx
src/
  Fugue.Core/           # доменные типы, конфиг, system-prompt; без UI и без сетевых deps
  Fugue.Agent/           # IChatClient factory, ручной conversation-loop, discovery
  Fugue.Tools/           # реализации tools как AIFunction
  Fugue.Cli/             # entry point: Terminal.Gui.Elmish MVU
tests/
  Fugue.Tests/           # xUnit + FsUnit
docs/
  superpowers/specs/2026-04-29-fugue-design.md   # этот документ
Directory.Build.props
README.md
```

Все проекты — F# `net10.0`, `IsAotCompatible=true`. Корневой `Directory.Build.props` включает `PublishAot=true`, `InvariantGlobalization=true`, source-generated JSON, `TreatWarningsAsErrors=true`.

### Fugue.Core — домен и конфиг

```fsharp
type Role = User | Assistant | System | Tool

type ContentBlock =
    | Text of string
    | ToolUse of id:string * name:string * argsJson:string
    | ToolResult of id:string * output:string * isError:bool

type Message = { Role: Role; Blocks: ContentBlock list; Timestamp: DateTimeOffset }

type ProviderConfig =
    | Anthropic of apiKey:string * model:string
    | OpenAI of apiKey:string * model:string
    | Ollama of endpoint:Uri * model:string

type AppConfig = {
    Provider: ProviderConfig
    SystemPrompt: string                       // дефолт = Fugue.Core.SystemPrompt.default'
    MaxIterations: int                          // safety-cap для tool-loop, дефолт 30
}
```

`Config.fs`:
- `Config.load : string[] -> Result<AppConfig, ConfigError>` — порядок: CLI-флаги → env → `~/.fugue/config.json` → discovery → fail.
- Сериализация через `System.Text.Json` source-generators (`[<JsonSerializable(typeof<AppConfigDto>)>]`), без reflection.

`SystemPrompt.fs`:
- Зашитый дефолт ~80 строк: cwd, OS, дата, инструкции по tool-use, формат ответов. Урезанная копия Claude Code system prompt'а.

### Fugue.Agent — провайдеры + ручной conversation-loop

#### ChatClientFactory

```fsharp
val create : ProviderConfig -> IChatClient
```

NuGet:
- `Microsoft.Extensions.AI` — абстракция `IChatClient`
- `Microsoft.Extensions.AI.OpenAI` (для OpenAI)
- `Anthropic.SDK` или официальный `Microsoft.Extensions.AI.Anthropic` (если стабилен) — для Anthropic
- `OllamaSharp` — для Ollama (имеет `IChatClient`-обёртку)

Разные пакеты возвращают `IChatClient` напрямую — фабрика просто диспатчится по `ProviderConfig`.

#### Discovery (Fugue.Agent.Discovery)

При `Config.load` без явного источника:

```fsharp
type Candidate =
    | EnvKey of provider:string * model:string * keySource:string
    | ClaudeSettings of model:string * apiKey:string
    | OllamaLocal of endpoint:Uri * models:string list

val discover : unit -> Candidate list
val promptUser : Candidate list -> Async<AppConfig option>    // stdin до Application.Init
val saveToConfigFile : AppConfig -> unit                       // ~/.fugue/config.json
```

Источники для MVP:
1. `ANTHROPIC_API_KEY` / `OPENAI_API_KEY` env.
2. `~/.claude/settings.json` — парсим только `env.ANTHROPIC_API_KEY` и `model`. **Не** трогаем `~/.claude/.credentials.json`.
3. HTTP GET `http://localhost:11434/api/tags` (timeout 200 мс) — если 200 OK, парсим `models[]`.

Если кандидатов 0 — печатаем help и exit 1.
Если ≥1 — численное меню в stdout, `[1] [2] … [q]`. После выбора пишем `~/.fugue/config.json` и продолжаем.

#### Conversation loop (ручной, по итогу brainstorming)

`Fugue.Agent.Conversation`:

```fsharp
type Event =
    | TextChunk of string
    | ToolStarted of id:string * name:string * argsJson:string
    | ToolCompleted of id:string * output:string * isError:bool
    | Finished
    | Failed of exn

val run :
    chatClient:IChatClient ->
    tools:AIFunction list ->
    history:ChatMessage list ->
    userInput:string ->
    cancel:CancellationToken ->
    IAsyncEnumerable<Event>
```

Внутри:
```
messages := history ++ [User: userInput]
loop:
    for update in chatClient.GetStreamingResponseAsync(messages, options with Tools=tools):
        match update.Contents:
            TextContent t        → yield TextChunk t
            FunctionCallContent  → запоминаем, не yield-им сразу
    собрали FunctionCallContent[] из последнего turn → если пусто: yield Finished, return
    для каждого call:
        yield ToolStarted
        result = invoke tool
        yield ToolCompleted
        messages ++= [Assistant with FunctionCallContent, Tool with FunctionResultContent]
    iter += 1; if iter > MaxIterations: yield Failed (TooManyIterations); return
```

Используем `IChatClient` напрямую, **не** `AIAgent.RunStreamingAsync`, чтобы UI видел каждый шаг. История между запусками — обычный `List<ChatMessage>`, нам не нужен `AgentThread` для MVP.

### Fugue.Tools

Каждый tool — F#-функция с `[<Description>]` на параметрах, обёрнутая через `AIFunctionFactory.Create` (из `Microsoft.Extensions.AI`). Все tools sandbox'ятся к `cwd`, абсолютные пути разрешены явно.

| Tool | Сигнатура | Реализация |
|---|---|---|
| `Read` | `path: string, ?offset: int, ?limit: int -> string` | `File.ReadAllLines`, нумерация `cat -n` стиле |
| `Write` | `path: string, content: string -> string` | `File.WriteAllText`, возвращает «wrote N bytes» |
| `Edit` | `path: string, old_string: string, new_string: string, ?replace_all: bool -> string` | exact-match замена; ошибка если old_string не уникален и replace_all=false |
| `Bash` | `command: string, ?timeout_ms: int -> string` | `Process.Start("/bin/zsh", "-lc", command)`, capture stdout+stderr, дефолт timeout 120 000 мс |
| `Glob` | `pattern: string, ?cwd: string -> string` | `Microsoft.Extensions.FileSystemGlobbing.Matcher`, sorted by mtime desc |
| `Grep` | `pattern: string, ?path: string, ?glob: string -> string` | shell-out на `rg` если есть в PATH; иначе .NET regex по дереву |

`ToolRegistry.allTools : AIFunction list` — список для передачи в conversation-loop.

`AIFunctionFactory.Create` использует reflection. Для AOT добавим `[<DynamicDependency>]` на каждой функции либо ручную обёртку через `AIFunction`-наследник без reflection (если warning'и пойдут).

### Fugue.Cli — TUI / MVU

#### Model

```fsharp
type ToolStatus = Running | Done of string | Failed of string

type Block =
    | UserMsg of string
    | AssistantText of StringBuilder           // мутируем при стриме
    | ToolBlock of id:string * name:string * args:string * status:ToolStatus
    | ErrorBlock of string

type Model = {
    Config: AppConfig
    Conversation: ChatMessage list             // канон для модели
    History: Block list                         // для UI
    Input: string
    Streaming: bool
    Cancel: CancellationTokenSource option
    Status: string
}
```

#### Msg

```fsharp
type Msg =
    | InputChanged of string
    | Submit
    | TextChunk of string
    | ToolStarted of id:string * name:string * args:string
    | ToolCompleted of id:string * output:string * isError:bool
    | StreamFinished
    | StreamFailed of string
    | Cancel
    | Quit
```

#### View (Terminal.Gui.Elmish DSL)

Layout:
```
┌─ Fugue — claude-opus-4-7 ─────────────── /Users/roman/proj ─┐
│ user> read README.md                                         │
│                                                              │
│ assistant> I'll read it.                                     │
│ ⏵ Read(README.md) ✓ 142 lines                                │   ← ScrollView с детьми-блоками
│ Here is the summary…                                         │
├──────────────────────────────────────────────────────────────┤
│ > █                                                          │   ← TextField, Shift+Enter = newline
└─ Enter: send · Shift+Enter: newline · Esc: cancel · ^C exit ─┘   ← StatusBar
```

- `View.window` корневой
- `View.scrollView` с детьми = `History |> List.map renderBlock`
  - `UserMsg` → `Label` синий с префиксом `user>`
  - `AssistantText` → `Label` дефолтный с префиксом `assistant>`
  - `ToolBlock` → `Label` `⏵ Name(args) status`, цвет по статусу (running жёлтый, done зелёный, failed красный)
  - `ErrorBlock` → `Label` красный
- `View.textField` снизу, `prop.value Input`, `prop.onChange (InputChanged)`, `prop.onEnter Submit`
- `View.statusBar` со shortcuts

При `TextChunk` — мутируем `StringBuilder` в последнем `AssistantText`-блоке (или создаём новый, если последний не такой). Перерисовка локальная.

Auto-scroll: `prop.ref` на `ScrollView`, после dispatch'а `TextChunk` / `ToolStarted` / `ToolCompleted` вызываем `view.SetNeedsDisplay()` + `view.ContentOffset` сдвигаем вниз.

#### Update

Стандартный MVU. `Submit` запускает `Cmd.ofSub`:

```fsharp
let submitCmd model dispatch =
    let cts = new CancellationTokenSource()
    Async.Start(async {
        try
            for evt in Conversation.run client tools model.Conversation model.Input cts.Token do
                match evt with
                | TextChunk t        -> dispatch (TextChunk t)
                | ToolStarted (i,n,a)-> dispatch (ToolStarted (i,n,a))
                | ToolCompleted (i,o,e) -> dispatch (ToolCompleted (i,o,e))
                | Finished           -> dispatch StreamFinished
                | Failed ex          -> dispatch (StreamFailed ex.Message)
        with :? OperationCanceledException -> dispatch (StreamFailed "cancelled")
    }, cts.Token)
    cts
```

Esc → `Cancel` → `model.Cancel |> Option.iter (fun cts -> cts.Cancel())`.

#### Bootstrap

```fsharp
[<EntryPoint>]
let main argv =
    match Config.load argv with
    | Error e -> eprintfn "%s" e; 1
    | Ok cfg ->
        let client = ChatClientFactory.create cfg.Provider
        let tools = ToolRegistry.allTools
        Program.mkProgram (Init.init cfg) (Update.update client tools) View.view
        |> Program.run
        0
```

## Data flow

```
keystroke → Msg.InputChanged → Model.Input ↑
Enter     → Msg.Submit
            → Cmd.ofSub (Conversation.run)
            → IChatClient streaming
            → Msg.TextChunk (×N) → мутирует AssistantText
            → Msg.ToolStarted   → добавляет ToolBlock(Running)
            → tool invocation (синхронно в loop)
            → Msg.ToolCompleted → меняет статус ToolBlock
            → next iteration LLM
            → Msg.StreamFinished → Streaming=false
Esc       → Msg.Cancel → CTS.Cancel() → loop ловит OCE → Msg.StreamFailed "cancelled"
^C / Quit → Application.RequestStop()
```

## Error handling

- `IChatClient` exception (network, auth, rate-limit) → `Msg.StreamFailed "<message>"` → `ErrorBlock` красный, `Streaming=false`. Conversation сохранена, можно продолжать новым вводом.
- Tool exception → `ToolCompleted (isError=true)`, в LLM уходит `tool_result` с `is_error=true`, модель сама комментирует.
- AOT-warning'и в `Fugue.Tools` от `AIFunctionFactory.Create` → митигация через `[<DynamicDependency>]`. Если не сработает — ручной `AIFunction`-наследник.
- Bash timeout → `ToolCompleted (output="<timeout>", isError=true)`.

## Testing

`Fugue.Tests` (xUnit + FsUnit):

- **Tools** — каждый против tmp-директории:
  - `Read` правильно нумерует, отдаёт offset/limit;
  - `Write` создаёт файл, возвращает байт-count;
  - `Edit` exact-match, fail на неуникальный old_string, replace_all работает;
  - `Bash` echo / exit-code / timeout;
  - `Glob` с `**/*.fs`;
  - `Grep` с regex и c фильтром по glob.
- **Config** — env override > file, JSON round-trip.
- **Discovery** — мок `~/.claude/settings.json`, мок Ollama-endpoint через `HttpMessageHandler`.
- **Conversation loop** — fake `IChatClient` возвращает заданный список update'ов, проверяем последовательность Event'ов и формирование `messages` history.

UI MVU тесты: `update`-функция чистая, тестируется без Terminal.Gui.

## Verification (acceptance)

1. `dotnet build` — без warnings.
2. `dotnet publish src/Fugue.Cli -c Release -r osx-arm64 /p:PublishAot=true` — собирается, итоговый бинарь < 60 МБ. AOT trim-warnings только в third-party (если есть).
3. `/usr/bin/time -l ./fugue` — RSS на старте < 80 МБ.
4. `ANTHROPIC_API_KEY=… ./fugue` без конфига — discovery-меню, выбор `[1]`, попадание в чат.
5. Чат «привет» → токены идут стримом, чат «прочитай README.md» → виден `⏵ Read(README.md) running` → `done 142 lines` → ответ ассистента.
6. Длинный ответ + Esc — `cancelled` блок, новый ввод работает.
7. `dotnet test` — все unit-тесты зелёные.
8. Меняем `~/.fugue/config.json` на Ollama (`llama3.1`) — те же сценарии работают (без tool-loop'а, если модель не поддерживает tool-use, ловим красивую ошибку).

## Risks & mitigations

| Риск | Митигация |
|---|---|
| Terminal.Gui.Elmish под AOT | Smoke-publish сразу после hello-world, до основного кода |
| `Microsoft.Agents.AI` prerelease — мы его не используем напрямую, остаёмся на `Microsoft.Extensions.AI` | Проверено в дизайне: factory + ручной loop поверх `IChatClient` достаточны |
| `AIFunctionFactory.Create` reflection и AOT | `[<DynamicDependency>]`, fallback — ручная `AIFunction`-обёртка |
| Anthropic NuGet — состояние пакетов | Если `Microsoft.Extensions.AI.Anthropic` не GA — берём `Anthropic.SDK` + тонкий адаптер к `IChatClient` |
| Ollama-провайдер не умеет function-calling | Фиксируем в README, при попытке tool-use печатаем понятную ошибку |
| F# DUs и AOT-сериализация | `JsonSerializerContext` source-gen, без `JsonSerializer.Serialize<obj>` |

## Out of scope (явно)

- Markdown-rendering, syntax-highlighting кода в чате
- Tab-completion в input'е
- Изображения / vision-модели
- Streaming тулзов (изменение output'а tool-call'а в реальном времени)
- Многооконный TUI (несколько чатов параллельно)
