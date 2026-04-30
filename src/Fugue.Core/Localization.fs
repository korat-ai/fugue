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
      HelpHeader:        string

      // /model suggest
      ModelSuggestPrompt: string
      AskingModelRecommendation: string

      // /review pr
      ReviewPrUsage:     string
      ReviewPrNotFound:  string
      ReviewPrPrompt:    string

      // Input safety
      ZeroWidthWarning: string }

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
      HelpHeader            = "Available slash commands:"
      ModelSuggestPrompt    = "Based on our conversation so far, what Fugue model configuration would you recommend? Consider: task complexity, whether deep reasoning is needed, response speed requirements, and cost efficiency. Fugue supports any OpenAI-compatible endpoint — suggest a specific model name if appropriate."
      AskingModelRecommendation = "Asking for model recommendation…"
      ReviewPrUsage         = "Usage: /review pr <N>"
      ReviewPrNotFound      = "PR not found or gh CLI unavailable"
      ReviewPrPrompt        = "Please review GitHub PR #{0}.\n\nPR metadata:\n{1}\n\nDiff:\n```\n{2}\n```\n\nProvide a thorough code review: identify bugs, style issues, missing tests, security concerns, and any improvements. Be specific with line references where possible."
      ZeroWidthWarning      = "Warning: input contains invisible zero-width characters — these may cause Edit tool diff mismatches" }

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
      HelpHeader            = "Доступные команды:"
      ModelSuggestPrompt    = "На основе нашего разговора, какую конфигурацию модели Fugue вы бы порекомендовали? Учтите: сложность задачи, необходимость глубокого рассуждения, требования к скорости ответа и эффективность по затратам. Fugue поддерживает любой OpenAI-совместимый эндпоинт — при необходимости предложите конкретное название модели."
      AskingModelRecommendation = "Запрашиваю рекомендацию по модели…"
      ReviewPrUsage         = "Использование: /review pr <N>"
      ReviewPrNotFound      = "PR не найден или gh CLI недоступен"
      ReviewPrPrompt        = "Пожалуйста, проверь GitHub PR #{0}.\n\nМетаданные PR:\n{1}\n\nDiff:\n```\n{2}\n```\n\nПроведи подробное ревью кода: выяви баги, проблемы со стилем, отсутствующие тесты, уязвимости безопасности и возможные улучшения. Указывай конкретные строки там, где это возможно."
      ZeroWidthWarning      = "Предупреждение: ввод содержит невидимые символы нулевой ширины — они могут нарушить работу инструмента Edit" }

/// Pick a Strings value by ISO-2 locale code. Unknown locales fall back to en.
let pick (locale: string) : Strings =
    match locale.ToLowerInvariant() with
    | "ru" -> ru
    | _    -> en
