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
      AtFileTooBig:   string }

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
      AtFileTooBig          = "[file too large — truncated to 4000 chars]" }

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
      AtFileTooBig          = "[файл слишком большой — обрезано до 4000 символов]" }

/// Pick a Strings value by ISO-2 locale code. Unknown locales fall back to en.
let pick (locale: string) : Strings =
    match locale.ToLowerInvariant() with
    | "ru" -> ru
    | _    -> en
