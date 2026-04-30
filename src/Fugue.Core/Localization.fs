module Fugue.Core.Localization

/// All UI strings of the app. Add new keys here, then populate both en and ru.
type Strings =
    { // Stream lifecycle
      Cancelled:    string
      ExitHint:     string
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
      DiscoveryPickProvider:  string

      // Slash commands
      CmdHelpDesc:       string
      CmdClearDesc:      string
      CmdNewDesc:        string
      CmdExitDesc:       string
      CmdModelDesc:      string
      CmdReviewPrDesc:   string
      CmdOnboardDesc:    string
      HelpHeader:        string

      // /issue command
      CmdIssueDesc:  string
      IssueNotFound: string
      IssueFetched:  string
      IssueUsage:    string

      // /model suggest
      ModelSuggestPrompt: string
      AskingModelRecommendation: string

      // /review pr
      ReviewPrUsage:     string
      ReviewPrNotFound:  string
      ReviewPrPrompt:    string

      // /git-log
      CmdGitLogDesc: string
      GitLogNoRepo:  string
      GitLogPrompt:  string

      // /ask
      CmdAskDesc: string
      AskUsage:   string

      // Input safety
      ZeroWidthWarning: string

      // @file injection
      AtFileNotFound: string
      AtFileTooBig:   string

      // @clipboard expansion
      ClipboardUnavailable: string

      // /diff command
      CmdDiffDesc: string
      NoDiff:      string

      // /init command
      CmdInitDesc: string
      InitExists:  string

      // /tools command
      CmdToolsDesc: string
      ToolsHeader:  string
      CmdClearHistoryDesc: string
      ClearHistoryDone:    string

      // /short /long verbosity
      CmdShortDesc:      string
      CmdLongDesc:       string
      VerbosityShortSet: string
      VerbosityLongSet:  string

      // /zen mode
      CmdZenDesc:  string
      ZenOn:       string
      ZenOff:      string

      // /snippet command
      CmdSnippetDesc:    string
      SnippetSaved:      string
      SnippetNotFound:   string
      SnippetInjected:   string
      SnippetNone:       string
      SnippetRemoved:    string
      SnippetUsage:      string

      // /bookmark command
      CmdBookmarkDesc:   string
      CmdBookmarksDesc:  string
      BookmarkSaved:     string
      BookmarkNotFound:  string
      BookmarkNone:      string
      BookmarkRemoved:   string
      BookmarkUsage:     string

      // /document command
      CmdDocumentDesc:   string
      DocumentPrompt:    string

      // /activity command
      CmdActivityDesc:   string
      ActivityNone:      string

      // /alias command
      CmdAliasDesc:   string
      AliasSet:       string
      AliasNone:      string
      AliasUsage:     string
      AliasRemoved:   string

      // /scratch command
      CmdScratchDesc:    string
      ScratchAppended:   string
      ScratchSent:       string
      ScratchCleared:    string
      ScratchEmpty:      string
      ScratchShowHeader: string

      // /summary
      CmdSummaryDesc: string

      // /squash
      CmdSquashDesc: string
      SquashUsage:   string
      SquashNoRepo:  string

      // /todo command
      CmdTodoDesc: string
      TodoNone:    string

      // /summarize command
      CmdSummarizeDesc: string

      // /note command
      CmdNoteDesc:  string
      NoteAdded:    string
      NoteNone:     string
      CmdNotesDesc: string

      // /undo command
      CmdUndoDesc:      string
      UndoNothingToUndo: string
      UndoRestored:     string
      UndoDeleted:      string

      // /find command
      CmdFindDesc:    string
      FindNoResults:  string
      FindInjectHint: string

      // @path injection hints
      TestFileHint:  string

      // Input prompt
      InputPlaceholder: string

      // Streaming waiting label
      Thinking: string

      // /watch command
      CmdWatchDesc:  string
      WatchAdded:    string
      WatchRemoved:  string
      WatchNone:     string
      WatchUsage:    string

      // /macro command
      CmdMacroDesc:   string
      MacroRecording: string
      MacroSaved:     string
      MacroPlaying:   string
      MacroNotFound:  string
      MacroNone:      string
      MacroRemoved:   string
      MacroUsage:     string

      // /theme command
      CmdThemeDesc:       string
      ThemeBubblesOn:     string
      ThemeBubblesOff:    string
      ThemeTypewriterOn:  string
      ThemeTypewriterOff: string
      ThemeUsage:         string

      // /compat command
      CmdCompatDesc: string

      // tool-call efficiency warning
      ToolCallsWarning: string }

let en : Strings =
    { Cancelled            = "cancelled"
      ExitHint             = "Press Ctrl+C again to exit"
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
      DiscoveryNoCandidates = "No providers detected."
      DiscoveryPickProvider = "Pick a provider:"
      CmdHelpDesc           = "show this help"
      CmdClearDesc          = "clear the screen"
      CmdNewDesc            = "start fresh conversation (new session)"
      CmdExitDesc           = "exit Fugue"
      CmdModelDesc          = "suggest best model for current task"
      CmdReviewPrDesc       = "fetch PR diff and generate AI review"
      CmdOnboardDesc        = "generate developer onboarding checklist"
      HelpHeader            = "Available slash commands:"
      CmdIssueDesc          = "fetch GitHub issue and inject context"
      IssueNotFound         = "issue not found or gh not available: {0}"
      IssueFetched          = "fetched issue #{0}: {1}"
      IssueUsage            = "usage: /issue <number>"
      ModelSuggestPrompt    = "Based on our conversation so far, what Fugue model configuration would you recommend? Consider: task complexity, whether deep reasoning is needed, response speed requirements, and cost efficiency. Fugue supports any OpenAI-compatible endpoint — suggest a specific model name if appropriate."
      AskingModelRecommendation = "Asking for model recommendation…"
      ReviewPrUsage         = "Usage: /review pr <N>"
      ReviewPrNotFound      = "PR not found or gh CLI unavailable"
      ReviewPrPrompt        = "Please review GitHub PR #{0}.\n\nPR metadata:\n{1}\n\nDiff:\n```\n{2}\n```\n\nProvide a thorough code review: identify bugs, style issues, missing tests, security concerns, and any improvements. Be specific with line references where possible."
      CmdGitLogDesc         = "explain recent git commit history"
      GitLogNoRepo          = "not a git repository or git unavailable"
      GitLogPrompt          = "Here are the last 20 commits from git log:\n\n%s\n\nPlease explain these commits in plain language — what changed, in what order, and what the overall direction seems to be."
      CmdAskDesc            = "ask a question without tool access (pure Q&A)"
      AskUsage              = "Usage: /ask <question>"
      ZeroWidthWarning      = "Warning: input contains invisible zero-width characters — these may cause Edit tool diff mismatches"
      AtFileNotFound        = "@{0}: file not found, skipping"
      AtFileTooBig          = "[file too large — truncated to 4000 chars]"
      ClipboardUnavailable  = "clipboard unavailable — pbpaste/xclip/wl-paste not found"
      CmdDiffDesc           = "show unstaged git diff (use /diff --staged for staged; /diff <a> <b> for two files)"
      NoDiff                = "no changes"
      CmdInitDesc           = "generate FUGUE.md for this project (creates if absent)"
      InitExists            = "FUGUE.md already exists. Edit it directly or delete it first."
      CmdToolsDesc          = "list all available tools with descriptions"
      ToolsHeader           = "Available tools"
      CmdClearHistoryDesc   = "reset conversation history without clearing screen"
      ClearHistoryDone      = "Conversation history cleared. Screen preserved."
      CmdShortDesc          = "switch to brief responses"
      CmdLongDesc           = "switch to detailed responses"
      VerbosityShortSet     = "Brevity mode on. Responses will be concise."
      VerbosityLongSet      = "Detail mode on. Responses will be thorough."
      CmdZenDesc            = "toggle distraction-free mode (hides status bar and tool output)"
      ZenOn                 = "Zen mode on. Status bar and tool output hidden."
      ZenOff                = "Zen mode off."
      CmdSnippetDesc        = "snippet save/list/inject/remove — named code pattern store"
      SnippetSaved          = "snippet saved: {0}"
      SnippetNotFound       = "snippet not found: {0}"
      SnippetInjected       = "injecting snippet: {0}"
      SnippetNone           = "no snippets saved"
      SnippetRemoved        = "snippet removed: {0}"
      SnippetUsage          = "usage: /snippet save <name> <file>:<start>-<end> | /snippet list | /snippet inject <name> | /snippet remove <name>"
      CmdBookmarkDesc       = "pin a symbol or file range for quick inject"
      CmdBookmarksDesc      = "list all bookmarks"
      BookmarkSaved         = "bookmarked: {0}"
      BookmarkNotFound      = "bookmark not found: {0}"
      BookmarkNone          = "no bookmarks"
      BookmarkRemoved       = "bookmark removed: {0}"
      BookmarkUsage         = "usage: /bookmark <name> [<file>:<start>-<end>] | /bookmarks | /bookmark remove <name>"
      CmdDocumentDesc       = "write session learnings to docs/ as a reference document"
      DocumentPrompt        = "Review our conversation and synthesize the key explanations, decisions, and insights into a well-structured reference document. Write it to {0} using your Write tool. Include: overview, key concepts explained, code examples from our session, and any important decisions or trade-offs discussed. Use clear Markdown with headers. If the target path does not exist, create it. Do not include conversational filler — this should read as a standalone reference doc."
      CmdActivityDesc       = "show file-edit heatmap for this session"
      ActivityNone          = "no file edits recorded this session"
      CmdAliasDesc          = "define session-scoped command shorthand (/alias cr = /review)"
      AliasSet              = "alias set: /{0} → {1}"
      AliasNone             = "no aliases defined"
      AliasUsage            = "usage: /alias <name> = <expansion> | /alias list | /alias remove <name>"
      AliasRemoved          = "alias removed: /{0}"
      CmdScratchDesc        = "multi-line composition buffer (append, send, clear, show)"
      ScratchAppended       = "[scratch: {0} lines]"
      ScratchSent           = "[scratch sent]"
      ScratchCleared        = "[scratch cleared]"
      ScratchEmpty          = "[scratch is empty]"
      ScratchShowHeader     = "scratch buffer:"
      CmdSummaryDesc        = "ask the agent to summarize the current session"
      CmdSquashDesc         = "generate squash commit message for last N commits"
      SquashUsage           = "usage: /squash <N>"
      SquashNoRepo          = "not a git repository or git unavailable"
      CmdTodoDesc           = "scan workspace for TODO/FIXME/HACK comments"
      TodoNone              = "no TODO/FIXME/HACK comments found in workspace"
      CmdSummarizeDesc      = "summarize a file or directory"
      CmdNoteDesc           = "attach a private annotation to the current turn"
      NoteAdded             = "note added"
      NoteNone              = "no notes yet"
      CmdNotesDesc          = "list all notes for this session"
      CmdUndoDesc           = "undo the most recent AI file write or edit"
      UndoNothingToUndo     = "nothing to undo"
      UndoRestored          = "restored"
      UndoDeleted           = "deleted (was new file)"
      CmdFindDesc           = "fuzzy-search workspace files by name or content"
      FindNoResults         = "no matches"
      FindInjectHint        = "type @<path> to inject a file into your next prompt"
      TestFileHint          = "[test file found: {0} — include it with @{1}]"
      InputPlaceholder      = "Type a message or /help"
      Thinking              = "thinking"
      CmdWatchDesc          = "watch a file and re-run a command on save"
      WatchAdded            = "watching {0} → will run: {1}"
      WatchRemoved          = "stopped watching {0}"
      WatchNone             = "no active watches"
      WatchUsage            = "usage: /watch <path> <command> | /unwatch <path> | /watches"
      CmdMacroDesc          = "record and replay multi-step command sequences"
      MacroRecording        = "recording macro '{0}'… run commands, then /macro stop"
      MacroSaved            = "macro '{0}' saved ({1} steps)"
      MacroPlaying          = "playing macro '{0}' ({1} steps)…"
      MacroNotFound         = "macro not found: {0}"
      MacroNone             = "no macros saved"
      MacroRemoved          = "macro removed: {0}"
      MacroUsage            = "usage: /macro record <name> | /macro stop | /macro play <name> | /macro list | /macro remove <name>"
      CmdThemeDesc          = "toggle display themes (/theme bubbles|typewriter)"
      ThemeBubblesOn        = "bubble borders on — AI responses wrapped in panels"
      ThemeBubblesOff       = "bubble borders off"
      ThemeTypewriterOn     = "typewriter effect on — responses fade in line by line"
      ThemeTypewriterOff    = "typewriter effect off"
      ThemeUsage            = "usage: /theme bubbles|typewriter"
      CmdCompatDesc         = "show terminal capability matrix"
      ToolCallsWarning      = "{0} tool calls this turn — consider breaking the task into smaller steps" }

let ru : Strings =
    { Cancelled            = "отменено"
      ExitHint             = "Нажми Ctrl+C ещё раз для выхода"
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
      DiscoveryNoCandidates = "Провайдеры не обнаружены."
      DiscoveryPickProvider = "Выберите провайдер:"
      CmdHelpDesc           = "показать эту справку"
      CmdClearDesc          = "очистить экран"
      CmdNewDesc            = "начать новый разговор (новая сессия)"
      CmdExitDesc           = "выйти из Fugue"
      CmdModelDesc          = "рекомендовать лучшую модель для текущей задачи"
      CmdReviewPrDesc       = "получить diff PR и сгенерировать AI-ревью"
      CmdOnboardDesc        = "сгенерировать чеклист онбординга разработчика"
      HelpHeader            = "Доступные команды:"
      CmdIssueDesc          = "получить задачу GitHub и добавить контекст"
      IssueNotFound         = "задача не найдена или gh недоступен: {0}"
      IssueFetched          = "получена задача #{0}: {1}"
      IssueUsage            = "использование: /issue <номер>"
      ModelSuggestPrompt    = "На основе нашего разговора, какую конфигурацию модели Fugue вы бы порекомендовали? Учтите: сложность задачи, необходимость глубокого рассуждения, требования к скорости ответа и эффективность по затратам. Fugue поддерживает любой OpenAI-совместимый эндпоинт — при необходимости предложите конкретное название модели."
      AskingModelRecommendation = "Запрашиваю рекомендацию по модели…"
      ReviewPrUsage         = "Использование: /review pr <N>"
      ReviewPrNotFound      = "PR не найден или gh CLI недоступен"
      ReviewPrPrompt        = "Пожалуйста, проверь GitHub PR #{0}.\n\nМетаданные PR:\n{1}\n\nDiff:\n```\n{2}\n```\n\nПроведи подробное ревью кода: выяви баги, проблемы со стилем, отсутствующие тесты, уязвимости безопасности и возможные улучшения. Указывай конкретные строки там, где это возможно."
      CmdGitLogDesc         = "объяснить историю git коммитов"
      GitLogNoRepo          = "не git-репозиторий или git недоступен"
      GitLogPrompt          = "Вот последние 20 коммитов из git log:\n\n%s\n\nОбъясни эти коммиты простым языком — что изменилось, в каком порядке, и каково общее направление разработки."
      CmdAskDesc            = "задать вопрос без использования инструментов (чистый Q&A)"
      AskUsage              = "Использование: /ask <вопрос>"
      ZeroWidthWarning      = "Предупреждение: ввод содержит невидимые символы нулевой ширины — они могут нарушить работу инструмента Edit"
      AtFileNotFound        = "@{0}: файл не найден, пропускаем"
      AtFileTooBig          = "[файл слишком большой — обрезано до 4000 символов]"
      ClipboardUnavailable  = "буфер обмена недоступен — pbpaste/xclip/wl-paste не найдены"
      CmdDiffDesc           = "показать unstaged git diff (используйте /diff --staged для staged; /diff <a> <b> для двух файлов)"
      NoDiff                = "изменений нет"
      CmdInitDesc           = "создать FUGUE.md для проекта (если отсутствует)"
      InitExists            = "FUGUE.md уже существует. Отредактируйте его напрямую или сначала удалите."
      CmdToolsDesc          = "показать список инструментов с описаниями"
      ToolsHeader           = "Доступные инструменты"
      CmdClearHistoryDesc   = "сбросить историю разговора без очистки экрана"
      ClearHistoryDone      = "История разговора сброшена. Экран сохранён."
      CmdShortDesc          = "перейти к кратким ответам"
      CmdLongDesc           = "перейти к подробным ответам"
      VerbosityShortSet     = "Режим краткости включён. Ответы будут краткими."
      VerbosityLongSet      = "Режим подробности включён. Ответы будут развёрнутыми."
      CmdZenDesc            = "переключить режим дзен (скрывает статус-бар и вывод инструментов)"
      ZenOn                 = "Режим дзен включён. Статус-бар и вывод инструментов скрыты."
      ZenOff                = "Режим дзен выключен."
      CmdSnippetDesc        = "сниппет save/list/inject/remove — хранилище именованных паттернов"
      SnippetSaved          = "сниппет сохранён: {0}"
      SnippetNotFound       = "сниппет не найден: {0}"
      SnippetInjected       = "вставляю сниппет: {0}"
      SnippetNone           = "сниппетов нет"
      SnippetRemoved        = "сниппет удалён: {0}"
      SnippetUsage          = "использование: /snippet save <имя> <файл>:<от>-<до> | /snippet list | /snippet inject <имя> | /snippet remove <имя>"
      CmdBookmarkDesc       = "закрепить символ или диапазон файла для быстрой вставки"
      CmdBookmarksDesc      = "список всех закладок"
      BookmarkSaved         = "закладка сохранена: {0}"
      BookmarkNotFound      = "закладка не найдена: {0}"
      BookmarkNone          = "закладок нет"
      BookmarkRemoved       = "закладка удалена: {0}"
      BookmarkUsage         = "использование: /bookmark <имя> [<файл>:<от>-<до>] | /bookmarks | /bookmark remove <имя>"
      CmdDocumentDesc       = "записать знания из сессии в docs/ как справочный документ"
      DocumentPrompt        = "Просмотри наш разговор и составь подробный справочный документ. Запиши его в {0} с помощью инструмента Write. Включи: обзор, ключевые концепции, примеры кода из нашей сессии, важные решения и компромиссы. Используй структурированный Markdown с заголовками. Если путь не существует — создай его."
      CmdActivityDesc       = "тепловая карта правок файлов за эту сессию"
      ActivityNone          = "в этой сессии файлов не редактировалось"
      CmdAliasDesc          = "псевдоним команды для сессии (/alias cr = /review)"
      AliasSet              = "псевдоним установлен: /{0} → {1}"
      AliasNone             = "псевдонимов не определено"
      AliasUsage            = "использование: /alias <имя> = <команда> | /alias list | /alias remove <имя>"
      AliasRemoved          = "псевдоним удалён: /{0}"
      CmdScratchDesc        = "буфер для составления сложных запросов (append, send, clear, show)"
      ScratchAppended       = "[scratch: {0} строк]"
      ScratchSent           = "[scratch отправлен]"
      ScratchCleared        = "[scratch очищен]"
      ScratchEmpty          = "[scratch пуст]"
      ScratchShowHeader     = "содержимое scratch:"
      CmdSummaryDesc        = "попросить агента резюмировать текущую сессию"
      CmdSquashDesc         = "сгенерировать squash-сообщение для последних N коммитов"
      SquashUsage           = "использование: /squash <N>"
      SquashNoRepo          = "не git-репозиторий или git недоступен"
      CmdTodoDesc           = "найти TODO/FIXME/HACK комментарии в проекте"
      TodoNone              = "TODO/FIXME/HACK комментарии не найдены"
      CmdSummarizeDesc      = "резюме файла или директории"
      CmdNoteDesc           = "добавить личную заметку к текущему обороту"
      NoteAdded             = "заметка добавлена"
      NoteNone              = "заметок пока нет"
      CmdNotesDesc          = "список всех заметок сессии"
      CmdUndoDesc           = "отменить последнее изменение файла ИИ"
      UndoNothingToUndo     = "нечего отменять"
      UndoRestored          = "восстановлен"
      UndoDeleted           = "удалён (был новым файлом)"
      CmdFindDesc           = "нечёткий поиск файлов по имени или содержимому"
      FindNoResults         = "совпадений не найдено"
      FindInjectHint        = "введите @<путь> чтобы добавить файл в следующее сообщение"
      TestFileHint          = "[найден тест-файл: {0} — подключите его через @{1}]"
      InputPlaceholder      = "Введите сообщение или /help"
      Thinking              = "думаю"
      CmdWatchDesc          = "следить за файлом и перезапускать команду при сохранении"
      WatchAdded            = "слежу за {0} → запущу: {1}"
      WatchRemoved          = "перестал следить за {0}"
      WatchNone             = "активных наблюдений нет"
      WatchUsage            = "использование: /watch <путь> <команда> | /unwatch <путь> | /watches"
      CmdMacroDesc          = "запись и воспроизведение последовательностей команд"
      MacroRecording        = "запись макроса '{0}'… введите команды, затем /macro stop"
      MacroSaved            = "макрос '{0}' сохранён ({1} шагов)"
      MacroPlaying          = "воспроизвожу макрос '{0}' ({1} шагов)…"
      MacroNotFound         = "макрос не найден: {0}"
      MacroNone             = "макросов нет"
      MacroRemoved          = "макрос удалён: {0}"
      MacroUsage            = "использование: /macro record <имя> | /macro stop | /macro play <имя> | /macro list | /macro remove <имя>"
      CmdThemeDesc          = "переключить визуальные темы (/theme bubbles|typewriter)"
      ThemeBubblesOn        = "рамки включены — ответы AI обёрнуты в панели"
      ThemeBubblesOff       = "рамки выключены"
      ThemeTypewriterOn     = "эффект машинки включён — ответы появляются построчно"
      ThemeTypewriterOff    = "эффект машинки выключен"
      ThemeUsage            = "использование: /theme bubbles|typewriter"
      CmdCompatDesc         = "показать матрицу возможностей терминала"
      ToolCallsWarning      = "{0} вызовов инструментов за ход — попробуйте разбить задачу на части" }

/// Pick a Strings value by ISO-2 locale code. Unknown locales fall back to en.
let pick (locale: string) : Strings =
    match locale.ToLowerInvariant() with
    | "ru" -> ru
    | _    -> en
