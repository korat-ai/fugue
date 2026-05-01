# Fugue — architecture review (2026-05-01)

> Принимающий: Roman. Триггер: «у нас очень много проблем именно с рендерингом, мы к нему подходим излишне легко». Это не аудит-on-demand, это диагноз болезни системы.

## TL;DR — три самых болезненных диагноза

1. **Нет single-owner для terminal-state.** Курсор, scroll-region, color-on/off, theme и активный layout — всё разбросано по 4 файлам с 77 ANSI-escape вкраплениями и **20+ module-level mutable**. Каждый "fix UI bug" — это реактивная игра в whack-a-mole, потому что нет инварианта который можно проверить, и нет места где гарантируется, что после операции X курсор окажется в позиции Y.
2. **`cfg` — не источник правды для рендеринга.** Render/MarkdownRender/StatusBar держат свои копии полей (theme, color, locale, low-bandwidth), синхронизируются через 4 разные функции (`initTheme`, `setCfg`, `initEmoji`, `setLowBandwidth`...), и забыть один из них — нормальный путь к багам сегодняшнего дня (`/locale` без `setCfg`, `/model set` без `setCfg`).
3. **TUI-рендеринг тестировать НЕЧЕМ.** 447 тестов проверяют чистые функции (`applyKey`, `previewTurnContent`, парсеры). Ни один не проверяет что после `redraw` курсор там, где должен быть. Поэтому каждый рендер-баг ловится глазами на real-TTY, после shipping.

---

## Сильные стороны (чтобы не делать вид что всё плохо)

- **Слоистая dependency direction** строгая: `Core ← Agent/Tools ← Cli`. AOT trim видит границы, нет цикл-зависимостей. Это редкое достижение для проектов, которые росли итеративно.
- **AOT discipline** — везде `DelegatedAIFunction` вместо `AIFunctionFactory.Create`, везде `Utf8JsonWriter`/`JsonDocument.Parse` вместо reflection-based serialization. Цена — verbose'ность каждого нового tool'а, но binary size и cold-start того стоят.
- **Persistence в три явных слоя** (JSONL / FTS5 / NDJSON) с симметричными env-overrides. Это правильное разделение responsibilities.
- **Hooks subsystem** — это серьёзный extension-point, спроектирован грамотно: stdin/stdout JSON, timeout, matcher syntax, отдельная `ContextProvider` категория с TTL-cache. Чище чем у конкурентов.
- **Picker.fs** (генерик scrollable picker) — пример того как ДОЛЖЕН выглядеть рендер-модуль: 117 LOC, чистая функция от состояния к ANSI-escapes, ясные инварианты. Используется тремя командами (`/turns`, `/menu`, `/model`).

---

## КРИТИЧЕСКИЕ слабости — рендеринг (priority focus)

### 1. Нет владельца terminal-state. Цена — каждое исправление лечит симптом

**Evidence:**
- `rg '\\x1b\\[' src/Fugue.Cli/` → **77 прямых ANSI-escape выписываний** в 4-х файлах: `Picker.fs`, `StatusBar.fs`, `ReadLine.fs`, `Repl.fs`.
- `Repl.fs` в этом списке — **архитектурное нарушение**. Repl должен быть dispatcher'ом, а не рендером.

**Симптомы которые мы сами видели сегодня:**
- `b-a3b` хвост в placeholder'е (ReadLine first-draw без `\x1b[2K`) — никто не гарантировал чистоту строки до записи
- `/model set` оставлял старую модель в StatusBar (`StatusBar.start` early-return при active) — нет API "обнови, если изменилось", только "запусти если ещё не запущен"
- `/locale` обновлял inline-строки но не StatusBar — две копии state синхронизировались разными командами
- Hot-reload `~/.fugue/config.json` обновлял всё кроме StatusBar — вторая итерация той же ошибки

**Корень проблемы**:

`StatusBar.fs:13-30` — **15 module-level mutable** с разной семантикой:
```fsharp
let mutable private cwd            // обновляется через start
let mutable private cfg            // обновляется через start (раньше) / setCfg (теперь)
let mutable private active         // обновляется через start/stop
let mutable private activeTheme    // обновляется через initTheme — ОТДЕЛЬНО от cfg
let mutable private compactMode    // вычисляется в start, никогда не пересчитывается
let mutable private branchCache    // self-managed cache
let mutable private streamingSince // обновляется через startStreaming/stopStreaming
let mutable private spinnerFrame
let mutable private sessionWords   // обновляется через recordWords/resetWords
let mutable private annotUpCount, annotDownCount
let mutable private lowBandwidthMode  // обновляется через setLowBandwidth
let mutable private templateName      // обновляется через setTemplateName
let mutable private scheduleActive    // обновляется через setScheduleActive
let mutable private spinnerTimer      // self-managed
let mutable private onTick            // self-managed
```

Каждое из этих полей имеет свой жизненный цикл, свой setter, и **никаких инвариантов между ними**. Ни один тест не проверяет, что после `setCfg` поле `compactMode` корректно (оно вычислено в `start` один раз, и при resize окна не пересчитывается).

**Цена**: при добавлении новой UI-фичи (badge, hint, hot-reload field) разработчик должен помнить **по меньшей мере 4 setter'а** + ещё `Render.init*` + ещё `MarkdownRender.init*`. Это не масштабируется.

### 2. Render-state как `record` вместо 20 mutables — простой архитектурный fix

`StatusBar.fs` + `Render.fs` + `MarkdownRender.fs` вместе держат **21 module-level mutable**. Должно быть:

```fsharp
type RenderState = {
    Cwd: string
    Cfg: AppConfig
    Active: bool
    Theme: string         // derived от cfg.Ui.Theme при apply
    ColorEnabled: bool    // derived от cfg + env (NO_COLOR, IsOutputRedirected, etc)
    BubblesMode: bool     // derived от cfg.Ui.BubblesMode
    Typewriter: bool      // derived от cfg.Ui.TypewriterMode
    EmojiNormalise: bool  // derived от cfg.Ui.EmojiMode + terminal capability
    CompactMode: bool     // derived от Console.WindowHeight, recomputed on SIGWINCH
    BranchCache: BranchCacheState
    Streaming: StreamingState
    Annotations: AnnotState
    LowBandwidth: bool    // derived от cfg
    TemplateName: string option
    ScheduleActive: bool
}
```

Один mutable `state: RenderState ref`, один `apply (newCfg: AppConfig) (state: RenderState ref) : unit` который **derive'ит** все полевые значения из `cfg` и переcomputes invariants. Все `initTheme`/`setCfg`/`setLowBandwidth` исчезают как отдельные функции.

**Bonus**: `apply` становится тестируемой pure-ish функцией: `(oldState, newCfg) → newState`. Можно проверять что после применения `cfg.Ui.Theme = "nocturne"` поле `state.Theme = "nocturne"`. Не нужен TTY.

### 3. Scroll-region — global terminal mutation без protocol

`StatusBar.start` пишет `\x1b[1;{h-3}r` — это **глобальная мутация терминала**: все последующие writes (включая от других модулей, включая агент-стрим в Repl) попадают в scroll-region 1..(h-3), а нижние 3 строки — terra incognita.

**Кто ещё пишет в нижние 3 строки?** Только StatusBar.refresh. **Кто это гарантирует?** Никто. Если ReadLine когда-нибудь захочет нарисовать что-то длиннее scrollable-region (multi-line input, suggestions ниже cursor) — оно может попасть на StatusBar и стереть его, или наоборот, StatusBar timer'ом сотрёт ReadLine'овский draw.

**Что должно быть**: `Surface` abstraction — модуль который владеет регионами:
```fsharp
type Region = ScrollableArea | StatusBarLine1 | StatusBarLine2 | ThinkingLine
type Surface = {
    paint: Region -> string -> unit
    moveTo: Region -> int -> unit  // column within region
    saveRestore: (unit -> 'a) -> 'a  // wraps \x1b[s ... \x1b[u
}
```

Все ANSI-escape вызовы прячутся за этим API. Никаких `\x1b[2K\r` в `ReadLine.fs:344`, никаких `\x1b[s ... \x1b[u` в `StatusBar.refresh`. Если кто-то из ReadLine хочет писать — он `paint ScrollableArea text` и это **гарантированно** не попадёт на StatusBar.

### 4. Race condition: timer-based refresh пишет в Console из background thread

`StatusBar.fs:135-142`:
```fsharp
let startStreaming () =
    streamingSince <- Some DateTime.UtcNow
    tokenQueue.Clear()
    spinnerFrame <- 0
    let t = new System.Threading.Timer(
                System.Threading.TimerCallback(fun _ -> onTick ()),
                null, 300, 300)
    spinnerTimer <- Some t
```

`onTick = refresh`, который вызывает `writeRaw` (который вызывает `Console.Out.Write`). Параллельно главный поток в `Conversation.run` тоже пишет в `Console.Out` (assistant stream). **Два writer'а на один shared resource без синхронизации** — это рецепт interleaved bytes на терминал.

**Симптом который МЫ ВИДИМ**: иногда ANSI-escape sequence "разрезается" пополам, и в выводе появляется literal `[2K` или `[31m` text. Это race на этой timer'е.

**Фикс**: либо `lock` вокруг `writeRaw`, либо timer должен только set'ить флаг `dirty: bool ref`, а реальный draw происходит только из главного потока в известных safe points (между token-стримом, после tool-call, перед ReadLine.read).

### 5. `Console.WindowWidth` / `WindowHeight` — нет invalidation на resize

Окно терминала может ресайзиться. На POSIX это SIGWINCH; .NET перехватывает и `Console.WindowWidth/Height` начинает возвращать новое значение. Но:

- `StatusBar.fs:231` вычисляет `compactMode <- height < 24 || width < 60` ОДИН РАЗ в `start`. После resize compactMode не обновляется → если юзер начал в широком окне и сжал в узкое, StatusBar пытается рисовать 3 строки в окне где их нет → каша.
- `StatusBar.refresh` каждый раз читает `Console.WindowHeight` (`line 153`) для позиционирования. ОК.
- `ReadLine.fs` хранит `Width` в `S` record (line 81) — это **snapshot на момент start**, не обновляется. После resize prompt-redraw использует старую ширину → wrap'ит не там.
- `Render.fs:114-116` `termWidth()` каждый раз читает live — ОК.

Несогласованность: одни модули читают live, другие cache'ируют snapshot. Resize ломает только cache'ирующих.

**Фикс**: subscribe на `Console.CancelKeyPress`-style hook для SIGWINCH (или периодический poll), invalidate snapshot, force redraw.

### 6. ReadLine + StatusBar договорённости неявны

`ReadLine.redraw` рисует input-prompt + slash-suggestions. **Ни одна строка кода не гарантирует** что результат draw'а помещается в scroll-region (1..h-3). Если у юзера 30 suggestions (а они есть — 80+ slash-команд), и окно высотой 25, draw распухает до 25 строк → переезжает в StatusBar-зону, стирает её, корраптит scroll-region.

Митигация в коде есть: `maxSuggestions = 8` (`ReadLine.fs:369, 381`). Но это **costюль** — выбран `8` потому что "обычно работает". Не выведено из реальной ширины и высоты доступной области.

**Должно быть**: ReadLine должен **спросить** Surface "сколько у меня доступно строк ниже prompt'а?" и сам решить сколько suggestions показать.

### 7. Theme propagation — 3 параллельные init-вызова

Сегодня тема устанавливается через 3 отдельных вызова в `Program.fs:88-91`:
```fsharp
MarkdownRender.initTheme cfg.Ui.Theme
Render.initTheme cfg.Ui.Theme
StatusBar.initTheme cfg.Ui.Theme
```

Если завтра появится 4-й рендер-модуль с темой (DiffRender? StackTraceRender?) — забыть его в этом списке = bug. И уже почти так и есть — `StackTraceRender.fs` существует, грепом не вижу `initTheme` в нём.

**Должно быть**: единый `RenderInit.applyAll cfg state`, который зовёт все рендер-модули по одному API.

### 8. Нет render-test harness — каждый bug ловится глазами

**Это корневая причина почему рендер-баги повторяются**. Цикл:
1. Юзер сообщает: "что-то поехало"
2. Я гипотетизирую причину
3. Subagent чинит и говорит "AOT clean, 447/447 tests"
4. Юзер нашёл новый bug на real-TTY
5. Goto 1

447 тестов проверяют:
- `ReadLine.applyKey` (pure dispatch)
- `previewTurnContent`, `cosineSimilarity`, парсеры
- `SessionPersistence` round-trip
- `SearchIndex` FTS5 round-trip

**Что НЕ проверено**:
- `ReadLine.redraw` — что после draw курсор в позиции X, что строки 0..N не содержат stale chars
- `StatusBar.refresh` — что 3 нижние строки после draw содержат ожидаемые токены
- `Render.assistantFinal` — что markdown → ANSI с правильным wrap'ом
- Любой инвариант "after operation Y, terminal state is Z"

**Что нужно**: in-process mock terminal. Принимает ANSI-escape входные пишутся, поддерживает `\x1b[H`, `\x1b[2K`, `\x1b[s/u`, scroll-region, `\x1b[N;MH`. После каждого `redraw` тест проверяет:
```fsharp
mockTerm.lineAt(15) |> should equal "♩ Type a message or /help"
mockTerm.cursorAt() |> should equal (row=20, col=27)
```

Не нужен TTY. Не нужен provider. CI-friendly.

**Это ЕДИНСТВЕННОЕ что реально остановит whack-a-mole**.

---

## Other architectural debt (вне рендеринга)

### 9. `Repl.fs` 3.2k LOC monolith

Match-block с 100+ arms, каждая команда инлайн. `streamAndRender` принимает 7 параметров. Любая попытка добавить shared контекст (e.g. передать `sessionRef` — мы это сделали сегодня) задевает каждую arm. 

**Recipe для refactor'а**: registry-based dispatch.
```fsharp
type Handler = HandlerCtx -> Task<HandlerResult>
let registry : Map<string, Handler> = Map.ofList [
    "/help",     handlers.Help
    "/turns",    handlers.Turns
    "/index",    handlers.Index
    ...
]
```

Каждый handler — отдельный модуль (`Handlers/Help.fs`). Repl.fs становится 200 LOC dispatcher'ом + StatusBar/ReadLine glue.

**Не делать одним PR**. Разбить на: (1) handler-table infrastructure, (2) перенос 5-10 простых команд, (3) перенос streaming-команд. Каждая фаза — 1 PR, проверяется отдельно.

### 10. `cfg` mutation pattern

`Repl.run` имеет `let mutable cfg = initialCfg` в closure'е. После `/model set` и пр. mutate'ится локальная копия. Дальше — manual broadcast: `StatusBar.setCfg`, `Render.initTheme`, etc.

В идеале — `cfgRef : AppConfig ref` где-то в Program.fs (как `sessionRef` для GetConversationFn), с явным subscriber-list:
```fsharp
let cfgRef = ref initialCfg
RenderState.subscribeTo cfgRef
StatusBar.subscribeTo cfgRef
// после изменения:
cfgRef.Value <- newCfg
notify cfgRef  // все subscriber'ы пересинхронизируются
```

Но это рефакторинг той же категории что #50 (closure capture), не разовый fix.

### 11. `savedUi` vs `cfg` — двойное состояние UI

В `Repl.run` есть и `cfg` (immutable snapshot изначальный) и `savedUi` (mutable UI-state, обновляется `/short`/`/long`/`/locale`/`/theme`). Подписан subagent A: `savedUi` ≠ `cfg.Ui` после первого изменения, и любая операция которая делает `{ cfg with Ui = savedUi }` для прокидывания в renderer — это hint что архитектура не правильная.

**Должно быть**: `cfg` — единственный mutable. `/short`/`/long` мутируют `cfg.Ui.Verbosity`. При мутации — полный broadcast.

### 12. `liveStrings` — третья копия UI state

`Repl.run` имеет `let mutable liveStrings = pick cfg.Ui.Locale`. После `/locale ru` — `liveStrings <- pick "ru"`. Это **третья копия** локали, отдельно от `cfg.Ui.Locale` и `savedUi.Locale`.

Та же проблема: разные пути обновления, разные времена жизни, легко рассинхронизировать.

### 13. AOT-discipline cost: STJ DTO boilerplate на каждом DTO

Каждый persisted record требует:
```fsharp
[<CLIMutable; DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)>]
type FooDto = { ... }
```

С `<NoWarn>IL2026</NoWarn>` в fsproj. 7 таких DTO в `JsonContext.fs` уже. Добавление нового — boilerplate task.

**Mitigation путь**: ждать F# JsonSerializerContext support (не скоро), или генерировать DTO через source-gen в C# и dependency-link'овать (не симпатично, нарушает F#-only правило).

**Verdict**: жить с болью. Документировать в CLAUDE.md как inevitable cost AOT.

### 14. Async race на `Async.Start` без Wait

`Repl.fs:584, 1684` — `Async.Start(async { ... })` для background-fetch модели. Нет ждущего вызова, нет cancellation если REPL exits. На session-end timer может ещё работать → пишет в Console после `StatusBar.stop()` → mess.

**Фикс**: использовать `cancelSrc.Token` который уже есть для streaming-cancel'а, привязать к нему все background async'и.

---

## Recommendations — приоритизировано

### Phase 1 — стопить кровотечение (1-2 PR'а, низкий риск)

1. **Render-test harness** — mock-terminal + первые 5 тестов на `ReadLine.redraw` и `StatusBar.refresh`. Без этого все остальные fix'ы — реактивные. Это **самый высокий ROI shot**.
2. ~~`lock` или dirty-flag для `StatusBar.refresh` timer race'а~~ — **superseded** см. Phase 3.

> **Update (после design discussion с Roman'ом)**: `lock` отбрасывается в пользу `MailboxProcessor`-based actor (см. Phase 3). Вместо точечного фикса race'а — делаем правильную абстракцию сразу. Phase 1 урезается до render-test harness как единственного действия.

### Phase 2 — устранить duplication (2-3 PR'а, средний риск)

3. **`RenderState` record** заменяющий 21 module-level mutable. Один `apply (newCfg) (state)` derive'ит всё. Удаляет 4 setter'а из публичного API.
4. **Унифицировать `cfg`/`savedUi`/`liveStrings`** в один mutable + auto-broadcast. Сегодняшние 2 fix'а от subagent A становятся ненужными.

### Phase 3 — Surface abstraction (новый sub-project, ИСПРАВЛЕННЫЙ DESIGN)

> **Updated 2026-05-02 после design discussion с Roman'ом.**

5. **Новый sub-project `src/Fugue.Surface/`** — отдельный fsproj в слое `Fugue.Core ← Fugue.Surface ← Fugue.Cli`. Может быть extracted в NuGet позже без обязательств.

6. **F# computation expression DSL** для drawing-ops:
   ```fsharp
   surface {
       paint Region.StatusBarLine1 (cwd + " on " + branch) Style.dim
       paint Region.StatusBarLine2 (providerLabel cfg) Style.dim
       eraseLine Region.ThinkingLine
   }
   // → возвращает DrawOp list (чистая функция, тестируется без TTY)
   ```

   AOT-чистота: builder = `Yield`/`Combine`/`Zero` методы, без reflection, без `MakeGenericMethod`. Подтверждается `dotnet publish` в Phase 0 spike.

7. **`MailboxProcessor`-based actor вместо `lock`'а** для serialization writes:
   ```fsharp
   type SurfaceMessage =
       | Execute of DrawOp list                                  // fire-and-forget
       | ExecuteAndAck of DrawOp list * AsyncReplyChannel<unit>  // sync barrier
       | Resize of int * int
       | Shutdown of AsyncReplyChannel<unit>

   let agent = MailboxProcessor.Start(fun inbox -> async { ... })
   ```

   **Преимущества vs lock**:
   - Producer'ы (Conversation stream, StatusBar timer, ReadLine, Picker) делают `agent.Post` без знания о синхронизации
   - **Coalescing встроен** — при 3 timer-tick'ах в очереди агент берёт только последний (меньше flicker'а)
   - **Приоритизация возможна** — keystroke перед spinner'ом (если потребуется)
   - **Нет dead-lock'ов** — producer не держит ничего после `Post`
   - Idiomatic F#

   **AOT-compat**: `MailboxProcessor` живёт в FSharp.Core, использует `Async<>`/`Queue<>`/`Interlocked` — все trim-clean. `Async<>` использует `AsyncStateMachineAttribute` (как `task<>`) — у нас уже AOT-clean, та же категория. Проверяем в Phase 0 spike.

   **Fallback** (если MailboxProcessor даст AOT-warning): `System.Threading.Channels.Channel<T>` с known-good AOT story, обернуть в F#-API — 30 LOC.

8. **Spectre.Console остаётся**, не выкидываем. Markdig → `IRenderable` → `DrawOp.AppendRenderable` → executor зовёт `AnsiConsole.Write` от своего имени. Spectre владеет **content rendering**, Surface владеет **placement и synchronization**.

9. **SIGWINCH handling** — отдельный msg `SurfaceMessage.Resize` приходит в агент при изменении размера окна. Агент пересчитывает регионы, отправляет всем подписчикам "redraw needed" event.

10. **Backpressure monitoring** — `MailboxProcessor` по умолчанию unbounded. Логируем `agent.CurrentQueueLength` в DebugLog при > 100. На нашем сценарии (50-200 tok/sec stream, 3 Hz timer) реальной угрозы нет, но монитор обязателен.

### Phase 0 — proof of concept (1 PR, ~3 дня, low risk)

Создать `src/Fugue.Surface/` с **только**:
- `Domain.fs` (`Region`, `Color`, `Style`, `DrawOp` DU)
- `Builder.fs` (`SurfaceBuilder` + custom-ops)
- `MockExecutor.fs` (для тестов)
- 10-15 тестов на builder
- AOT publish проверка: `dotnet publish src/Fugue.Surface -c Release -r osx-arm64`

**Не трогать существующий код**. Цель — proof что DSL + MailboxProcessor работают AOT-clean, и форма API устраивает.

Решение go/no-go на дальнейшие фазы — после Phase 0.

### Phase 4 — Repl decomposition (3-5 PR'ов, высокий risk)

7. **Handler-registry refactor**. Не делать пока Phase 1-3 не закончились — иначе будете чинить тот же баг в двух местах.

### Phase 5 — долгосрочное

8. **Streaming render contract** — `streamAndRender` сжать с 7 параметров до 2 (сontext-record + cancel-token). Возможно после Phase 4.
9. **AOT-DTO codegen** — может быть, F# 9 source-gen достаточно гибкий чтобы генерить DTO-обёртки. Investigate.

---

## Что НЕ делать (anti-patterns которые могут возникнуть)

- ❌ **Переписать рендеринг "с нуля"**. 11k LOC. Слишком много. Каждый fix — тонкий, постепенный.
- ❌ **Force-fix через `lock` everywhere**. Lock'и в hot-path UI = lag. Нужен design, не таблеточный фикс.
- ❌ **Refactor Repl.fs одним PR'ом**. 3.2k LOC. Diff будет 3.2k+. Code-review невозможен. **Гарантированно** введёт регрессии.
- ❌ **Полагаться "на soak"**. 14-day window полезен для real-user feedback на feature-completeness, не для отлова render-bug'ов. Render-bug'ам нужны **автотесты**, не люди.

---

## Заключение

Проблема не в "плохо написанных" функциях — большинство кода читаемое, F#-идиоматичное, AOT-disciplined. Проблема в **отсутствующем уровне абстракции** между "хочу нарисовать prompt" и "пиши `\x1b[2K\r`". Этот уровень должен называться `Surface` (или `Terminal`, или `Screen`), владеть курсором/регионами/синхронизацией, и иметь test harness.

Без этого слоя каждое исправление — реактивное. С ним — render-bug ловится тестом до merge.

**Самое важное**: **render-test harness — Phase 1 task**, всё остальное ждёт. Сейчас мы тушим пожары в темноте; harness даёт фонарик.

— architecture-reviewer, 2026-05-01
