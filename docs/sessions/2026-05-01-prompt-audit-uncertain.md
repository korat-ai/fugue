# PROMPT Audit — Uncertain Cases (требуют решения пользователя)

Третий проход (aggressive bias) пометил эти как STRONG PROMPT, но я не уверен.
Каждый кейс — реальное ограничение существующего toolset'а.

## ❓ Сомнительные — нужно решить, лейблить или нет

### Доступ к истории диалога (нет тулы)

Агент не имеет тулы для чтения собственной истории сообщений. Эти фичи требуют либо новой тулы, либо встроенной поддержки в REPL.

- **#104** — `/btw` side questions
  - Aggressive говорит: "extracts last 20 messages as context"
  - Реальность: агент не видит свою историю как файл, она в `AgentThread`. Нужна F#-тула `GetConversation` или сохранение каждого turn в файл.
  - **Моё мнение**: НЕ PROMPT, нужен код или новая тула.

- **#109** — `/compress` history
  - Aggressive говорит: "summarize current conversation, write to FUGUE.md, discard prior turns"
  - Реальность: "discard prior turns" — это REPL state mutation. Агент не может удалить свой контекст.
  - **Моё мнение**: PROMPT (часть про summary) + CODE (часть про reset). Это AMBIGUOUS.

- **#167** — `/export session`
  - Реальность: то же что #104 — нет тулы для чтения transcript'а.
  - **Моё мнение**: НЕ PROMPT.

### REPL input parsing (физический ввод)

`@something` синтаксис обрабатывается ДО того как агент видит сообщение. Это парсер `expandAtFiles` в Repl.fs.

- **#367** — `@url` context injection
  - Aggressive: "agent detects `@https://...`, fetches via curl"
  - Реальность: префикс `@` парсится в `Repl.fs:expandAtFiles` ДО отправки агенту. Без F#-кода в этом парсере `@url` останется буквальной строкой.
  - **Моё мнение**: НЕ PROMPT — нужно расширить парсер.

- **#370** — `@clipboard` injection
  - То же что #367.
  - **Моё мнение**: НЕ PROMPT.

### Зависят от hooks (#21)

- **#383** — Auto-format on write (PostToolUse hook)
  - Aggressive: "hook content is PROMPT, only registration needs F#"
  - Реальность: hooks ещё не реализованы. Без них фича не работает.
  - **Моё мнение**: AMBIGUOUS, зависит от #21. Если #21 решён — это PROMPT. Сейчас — нет.

- **#191** — Context providers
  - Aggressive: "FUGUE.md lists shell scripts, agent calls at session start"
  - Реальность: "at session start" = lifecycle hook = #21.
  - **Моё мнение**: AMBIGUOUS, та же логика что #383.

### Multi-context orchestration

- **#787** — `/ab-prompt`
  - Aggressive: "agent runs two separate analyses with two prompts"
  - Реальность: один агент в одной сессии не может запустить себя с другим system-prompt'ом. Нужна subagent-инфра (#25).
  - **Моё мнение**: НЕ PROMPT.

### Уже реализовано или конфликтует

- **#571** — `/model suggest`
  - Уже реализовано в Repl.fs (line 1507).
  - **Моё мнение**: Закрыть как done, не лейблить.

- **#568** — System prompt profiles
  - Частично уже есть `--profile` флаг.
  - **Моё мнение**: Прочитать issue полностью; возможно дубль.

### Сессии-как-файлы

- **#867** — `/faq` (Grep против session files)
  - Aggressive: "Grep against `~/.fugue/sessions/`"
  - Реальность: сессии хранятся ли вообще как файлы? Нужно проверить.
  - **Моё мнение**: зависит от того, есть ли persistence; вероятно требует код для сохранения сессий.

---

## Решение пользователя

Просьба пройтись по списку и сказать какие лейблить как PROMPT, какие оставить, какие закрыть как dup/done.
