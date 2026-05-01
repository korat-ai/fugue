# Hooks

Hooks are user-defined scripts that run at key lifecycle points in a Fugue session. They receive structured JSON context via stdin and can log, notify, or integrate with external systems. Phase 1 ships `SessionStart` and `SessionEnd` hooks; Phase 2 adds `PreToolUse` and `PostToolUse` hooks to intercept and modify tool invocations; Phase 3 adds `UserPromptSubmit` hooks to filter and enrich user prompts before agent processing.

## Configuration

Hooks are defined in `~/.fugue/hooks.json`. The file is optional; if absent, no hooks run.

### Schema

```json
{
  "hookTimeoutMs": 5000,
  "onError": "log-continue",
  "hooks": {
    "SessionStart": [
      {
        "type": "command",
        "command": "/path/to/script.sh",
        "async": false,
        "timeoutMs": 3000
      }
    ],
    "SessionEnd": [
      {
        "type": "command",
        "command": "~/.fugue/hooks/notify.sh",
        "async": true
      }
    ],
    "PreToolUse": [
      {
        "type": "command",
        "command": "~/.fugue/hooks/validate.sh",
        "async": false,
        "matcher": "Bash"
      }
    ],
    "PostToolUse": [
      {
        "type": "command",
        "command": "~/.fugue/hooks/audit.sh",
        "matcher": "*"
      }
    ],
    "UserPromptSubmit": [
      {
        "type": "command",
        "command": "~/.fugue/hooks/enrich-prompt.sh",
        "async": false
      }
    ]
  }
}
```

### Top-level fields

| Field | Type | Default | Notes |
|-------|------|---------|-------|
| `hookTimeoutMs` | int | 5000 | Global timeout (ms) for synchronous hooks. Overridden per-hook via `timeoutMs`. |
| `onError` | string | `"log-continue"` | How to handle hook failures: `"log-continue"` (warn, keep going) or `"log-block"` (fail the session). |
| `hooks` | object | `{}` | Container for per-event hook arrays. Possible keys: `SessionStart`, `SessionEnd`, `PreToolUse`, `PostToolUse`, `UserPromptSubmit`. |

### Hook definition (per hook in an array)

| Field | Type | Default | Notes |
|-------|------|---------|-------|
| `type` | string | — | Must be `"command"`. |
| `command` | string | — | Path or command to execute. Supports `~/` prefix. No shell quoting; space splits exe from args (first space only). |
| `async` | bool | false | If true, fire-and-forget; if false, Fugue waits up to `timeoutMs`. Ignored for `PostToolUse` and `UserPromptSubmit` (always sync). |
| `timeoutMs` | int | (global) | Per-hook override for `hookTimeoutMs`. Ignored for async hooks. |
| `matcher` | string | (all tools) | Restrict hook to a specific tool by exact name (e.g., `"Bash"`, `"Write"`), or `"*"` to match all tools. **Does not apply to `UserPromptSubmit` hooks.** |

## Events

### SessionStart

**Fires:** Immediately after the agent session is created, before the first prompt is shown to the user.

**Blocking:** No. Non-zero exit code logs a warning (or fails the session, depending on `onError`); session continues.

**Stdin payload:**

```json
{
  "event": "SessionStart",
  "version": 1,
  "cwd": "/home/alice/projects/myapp",
  "model": "claude-sonnet-4-6",
  "provider": "anthropic",
  "sessionId": "01HVZK7F4X0000000000000000"
}
```

| Field | Type | Notes |
|-------|------|-------|
| `event` | string | Always `"SessionStart"`. |
| `version` | int | Protocol version (1). Use for forward compatibility. |
| `cwd` | string | Working directory Fugue is running in. |
| `model` | string | LLM model name (e.g., `"claude-opus-4-1"`). |
| `provider` | string | Provider slug (e.g., `"anthropic"`, `"openai"`, `"ollama"`). |
| `sessionId` | string | Unique session identifier (ULID). |

### SessionEnd

**Fires:** In the session cleanup phase, after the final agent turn completes and before Fugue exits.

**Blocking:** No. Async hooks (fire-and-forget) are launched but not awaited.

**Stdin payload:**

```json
{
  "event": "SessionEnd",
  "version": 1,
  "cwd": "/home/alice/projects/myapp",
  "durationMs": 42310,
  "turnCount": 7
}
```

| Field | Type | Notes |
|-------|------|-------|
| `event` | string | Always `"SessionEnd"`. |
| `version` | int | Protocol version (1). Use for forward compatibility. |
| `cwd` | string | Working directory Fugue was running in. |
| `durationMs` | int | Total session duration in milliseconds. |
| `turnCount` | int | Number of agent turns (user prompts + responses). |

### PreToolUse

**Fires:** Before every tool invocation (Read, Write, Edit, Bash, Glob, Grep).

**Blocking:** Yes. If your script returns `{"block": true}`, the tool call is aborted and the agent sees the failure reason in the tool result. Useful for safety gates, permission checks, or command validation.

**Stdin payload:**

```json
{
  "event": "PreToolUse",
  "version": 1,
  "tool": "Bash",
  "args": {
    "command": "rm -rf /"
  },
  "sessionId": "01HVZK7F4X0000000000000000"
}
```

| Field | Type | Notes |
|-------|------|-------|
| `event` | string | Always `"PreToolUse"`. |
| `version` | int | Protocol version (1). Use for forward compatibility. |
| `tool` | string | Tool name (e.g., `"Bash"`, `"Write"`, `"Read"`, `"Edit"`, `"Glob"`, `"Grep"`). |
| `args` | object | Tool arguments as a JSON object. Structure varies by tool. |
| `sessionId` | string | Unique session identifier (ULID). |

**Hook script result protocol:** Your script outputs JSON to stdout and exits with code 0. Supported fields:

```json
{
  "block": false,
  "reason": "optional explanation",
  "modified_args": {
    "command": "echo safe"
  }
}
```

| Field | Type | Meaning |
|-------|------|---------|
| `block` | bool | If true, abort the tool call and return `reason` as the tool result (agent sees failure). Default false. |
| `reason` | string | Failure explanation shown to the LLM when `block: true`. Ignored if `block: false`. |
| `modified_args` | object | Override/add tool arguments. Keys are merged into the original tool args. Ignored if `block: true`. |

If your script outputs no JSON, it is treated as `{"block": false}` (proceed unchanged). Non-zero exit codes are treated as errors per `onError` policy.

### PostToolUse

**Fires:** After every tool invocation, on both success and failure paths.

**Blocking:** No. PostToolUse hooks are always fire-and-forget (async). The tool result is already visible to the agent; your script cannot modify it. Useful for audit logs, notifications, post-write formatting, or metrics collection.

**Stdin payload:**

```json
{
  "event": "PostToolUse",
  "version": 1,
  "tool": "Write",
  "args": {
    "path": "/home/alice/file.txt",
    "content": "..."
  },
  "result": "Written 42 bytes to /home/alice/file.txt",
  "isError": false,
  "sessionId": "01HVZK7F4X0000000000000000"
}
```

| Field | Type | Notes |
|-------|------|-------|
| `event` | string | Always `"PostToolUse"`. |
| `version` | int | Protocol version (1). Use for forward compatibility. |
| `tool` | string | Tool name (e.g., `"Write"`, `"Bash"`, etc.). |
| `args` | object | Tool arguments as a JSON object (same as PreToolUse). |
| `result` | string | The tool's result or error message (as a string). |
| `isError` | bool | True if the tool exited with an error, false on success. |
| `sessionId` | string | Unique session identifier (ULID). |

**Hook script result:** Your script's exit code and output are not processed. It runs in the background. Use for side effects only (logging, notifications, spawning formatters, etc.).

### UserPromptSubmit

**Fires:** After the user enters a prompt (with `@file` expansion, clipboard paste, and verbosity prefix applied) but BEFORE the prompt is dispatched to the agent.

**Blocking:** Yes. If your script returns `{"block": true}`, the turn is cancelled and the user sees the reason in the REPL. Useful for prompt validation, blocking sensitive content, or preventing accidental submissions.

**Important:** This hook intercepts **natural-language prompts only**. It does NOT fire for:
- Slash commands (`/help`, `/clear`, `/new`, `/exit`, etc.)
- Macro replay (`@recent`, `@file` initial fetch)
- Shell shortcut (`!cmd`)
- `/scratch send` command

See [What is intercepted](#what-is-intercepted) for details.

**Stdin payload:**

```json
{
  "event": "UserPromptSubmit",
  "version": 1,
  "prompt": "what changed in the last commit?",
  "sessionId": "01HVZK7F4X0000000000000000"
}
```

| Field | Type | Notes |
|-------|------|-------|
| `event` | string | Always `"UserPromptSubmit"`. |
| `version` | int | Protocol version (1). Use for forward compatibility. |
| `prompt` | string | The user's prompt text (after `@file` and clipboard expansion, but before agent processing). |
| `sessionId` | string | Unique session identifier (ULID). |

**Hook script result protocol:** Your script outputs JSON to stdout and exits with code 0. Supported fields (exclusive: only one of `block`, `replace`, or `append` applies):

```json
{
  "block": false,
  "reason": "optional explanation",
  "replace": "new prompt",
  "append": "additional context"
}
```

| Field | Type | Meaning |
|-------|------|---------|
| `block` | bool | If true, cancel the turn and display `reason` to the user. Default false. |
| `reason` | string | Message shown to user when `block: true`. Ignored if `block: false`. |
| `replace` | string | Substitute the prompt entirely with this text. Ignored if `block: true`. Only one of `replace` or `append` applies; if both are present, `replace` wins. |
| `append` | string | Append this text to the original prompt (newline-separated). Ignored if `block: true` or `replace` is present. Multiple hooks can append; their outputs are concatenated. |

If your script outputs no JSON, it is treated as `{}` (pass through unchanged). Non-zero exit codes are treated as errors per `onError` policy.

### What is intercepted

UserPromptSubmit hooks intercept **only natural-language prompts to the agent**:

| Input | Intercepted | Notes |
|-------|-------------|-------|
| `how do I refactor this?` (user prompt) | Yes | Normal agent query. |
| `what changed` (user prompt, mentions "changed") | Yes | Hooks can enrich this with `git status`. |
| `/help` (slash command) | No | Slash commands bypass hooks. |
| `/clear` (slash command) | No | Slash commands bypass hooks. |
| `!git log` (shell shortcut) | No | Shell shortcuts bypass hooks. |
| `@recent 3` (macro) | No | Macro replay bypasses hooks. |
| `/scratch send <Cmd>` (scratch command) | No | Specialized repl command, not a user prompt. |

If you need to intercept **all input including slash commands**, that is a future enhancement (Phase 4+). For now, UserPromptSubmit only sees natural-language prompts.

## Matcher syntax

The `"matcher"` field restricts a hook to specific tools:

- **Omitted or `"*"`** — Hook fires for all tools (SessionStart, SessionEnd, and all six tool types).
- **Exact name** — `"matcher": "Bash"` fires only before/after Bash tool invocations. Same for `"Read"`, `"Write"`, `"Edit"`, `"Glob"`, `"Grep"`.
- **No glob/regex in Phase 2** — Matchers use exact string equality only. Patterns like `"B*sh"`, `"Bash|Read"`, or `[R]ead` are not yet supported. If your `hooks.json` contains `|`, `?`, or `[` in a matcher, Fugue will warn at load time: *"matcher contains reserved metacharacters — only exact match is supported in this version"*. These are reserved for future versions (glob or regex patterns). For now, if you need to match multiple tools, create separate hook entries.

**Note:** `matcher` does **not** apply to `UserPromptSubmit` hooks. All UserPromptSubmit hooks fire for every natural-language prompt.

## Result protocol summary

Different hook types support different result fields. Here's a quick reference:

| Hook | Supported fields | Semantics |
|------|------------------|-----------|
| `SessionStart` | (none) | Informational; exit code and output ignored. |
| `SessionEnd` | (none) | Informational; exit code and output ignored. |
| `PreToolUse` | `block`, `reason`, `modified_args` | `block` aborts tool; `modified_args` changes tool behavior. |
| `PostToolUse` | (none) | Fire-and-forget; results ignored. |
| `UserPromptSubmit` | `block`, `reason`, `replace`, `append` | `block` cancels turn; `replace` overwrites prompt; `append` adds context. |

Fields are **exclusive** where noted:
- **PreToolUse:** `block: true` overrides `modified_args`.
- **UserPromptSubmit:** `block: true` overrides `replace` and `append`; `replace` overrides `append`.

## Worked examples

### Example 1: Audit log for all tools

Create `~/.fugue/hooks/audit.sh`:

```bash
#!/bin/bash
set -e
read -r payload
event=$(echo "$payload" | jq -r '.event')
case "$event" in
  PostToolUse)
    tool=$(echo "$payload" | jq -r '.tool')
    is_error=$(echo "$payload" | jq -r '.isError')
    session_id=$(echo "$payload" | jq -r '.sessionId')
    timestamp=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
    echo "$timestamp [$tool] isError=$is_error sessionId=$session_id" >> "$HOME/.fugue/audit.log"
    ;;
esac
exit 0
```

Add to `~/.fugue/hooks.json`:

```json
{
  "hooks": {
    "PostToolUse": [
      {
        "type": "command",
        "command": "~/.fugue/hooks/audit.sh",
        "matcher": "*"
      }
    ]
  }
}
```

Each tool invocation appends a line to `~/.fugue/audit.log`:

```
2026-05-01T14:23:15Z [Bash] isError=false sessionId=01HVZK7F...
2026-05-01T14:23:16Z [Write] isError=false sessionId=01HVZK7F...
```

### Example 2: Auto-format on Write

Create `~/.fugue/hooks/format.sh`:

```bash
#!/bin/bash
read -r payload
tool=$(echo "$payload" | jq -r '.tool')
if [ "$tool" = "Write" ]; then
  path=$(echo "$payload" | jq -r '.args.path')
  # Format the file after write (requires dotnet installed)
  dotnet format --include "$path" 2>/dev/null || true
fi
exit 0
```

Add to `~/.fugue/hooks.json`:

```json
{
  "hooks": {
    "PostToolUse": [
      {
        "type": "command",
        "command": "~/.fugue/hooks/format.sh",
        "matcher": "Write"
      }
    ]
  }
}
```

Note: argument substitution (e.g., extracting `args.path` and passing it to `dotnet format`) is your responsibility — jq handles the parsing. Fugue passes the raw JSON; your script decides how to use it.

### Example 3: Block dangerous Bash commands

Create `~/.fugue/hooks/bash-guard.sh`:

```bash
#!/bin/bash
read -r payload
command=$(echo "$payload" | jq -r '.args.command')
# Block rm -rf, mkfs, dd if=/dev/zero, etc.
if echo "$command" | grep -qE '(rm\s+-rf|mkfs|dd\s+if=/dev/zero)'; then
  echo '{"block": true, "reason": "destructive command rejected by bash-guard"}'
  exit 0
fi
echo '{"block": false}'
exit 0
```

Add to `~/.fugue/hooks.json`:

```json
{
  "hooks": {
    "PreToolUse": [
      {
        "type": "command",
        "command": "~/.fugue/hooks/bash-guard.sh",
        "async": false,
        "matcher": "Bash"
      }
    ]
  }
}
```

If the agent tries to run `rm -rf /home`, the hook blocks it:

```
[hook-blocked] destructive command rejected by bash-guard
```

The agent sees this as the tool result and can decide to retry with a safer command.

### Example 4: Enrich prompts with git status

Create `~/.fugue/hooks/enrich-prompt.sh`:

```bash
#!/bin/bash
read -r payload
prompt=$(echo "$payload" | jq -r '.prompt')

# Check if the prompt mentions "changed" or "diff"
if echo "$prompt" | grep -qiE '(changed|diff|status)'; then
  # Append git status to the prompt
  changes=$(git status -s 2>/dev/null | head -10)
  if [ -n "$changes" ]; then
    printf '{"append": "\n[git status]\n%s"}\n' "$changes"
  else
    echo '{}'
  fi
else
  # No enrichment needed
  echo '{}'
fi
exit 0
```

Add to `~/.fugue/hooks.json`:

```json
{
  "hooks": {
    "UserPromptSubmit": [
      {
        "type": "command",
        "command": "~/.fugue/hooks/enrich-prompt.sh",
        "async": false
      }
    ]
  }
}
```

When the user types `what changed?`, the hook intercepts it and appends the output of `git status -s`:

```
User input: what changed?
Hook appends: [git status]
                M src/foo.rs
                A src/bar.rs

Agent receives: what changed?
[git status]
M src/foo.rs
A src/bar.rs
```

The agent can now see the uncommitted changes and respond accurately.

## Writing a hook script

Hook scripts are normal executables (shell, Python, Go, etc.). They read JSON from stdin and process it. Exit code `0` signals success; non-zero signals failure (handled per `onError`). For PreToolUse and UserPromptSubmit hooks, your script's stdout is parsed as JSON; for PostToolUse, SessionStart, and SessionEnd hooks, stdout is typically ignored (but can be logged if present on stderr).

### Example: Session audit log (SessionStart + SessionEnd)

Create `~/.fugue/hooks/session-audit.sh`:

```bash
#!/bin/bash
set -e

# Read JSON from stdin
read -r payload

# Parse fields (requires jq)
event=$(echo "$payload" | jq -r '.event')
version=$(echo "$payload" | jq -r '.version')
cwd=$(echo "$payload" | jq -r '.cwd')
timestamp=$(date -u +"%Y-%m-%dT%H:%M:%SZ")

logfile="$HOME/.fugue/sessions.log"

case "$event" in
  SessionStart)
    model=$(echo "$payload" | jq -r '.model')
    provider=$(echo "$payload" | jq -r '.provider')
    session_id=$(echo "$payload" | jq -r '.sessionId')
    echo "$timestamp [$event] model=$model provider=$provider cwd=$cwd sessionId=$session_id" >> "$logfile"
    ;;
  SessionEnd)
    duration_ms=$(echo "$payload" | jq -r '.durationMs')
    turn_count=$(echo "$payload" | jq -r '.turnCount')
    echo "$timestamp [$event] durationMs=$duration_ms turnCount=$turn_count cwd=$cwd" >> "$logfile"
    ;;
esac

exit 0
```

Make it executable:

```bash
chmod +x ~/.fugue/hooks/session-audit.sh
```

Add to `~/.fugue/hooks.json`:

```json
{
  "hookTimeoutMs": 5000,
  "onError": "log-continue",
  "hooks": {
    "SessionStart": [
      {
        "type": "command",
        "command": "~/.fugue/hooks/session-audit.sh",
        "async": false
      }
    ],
    "SessionEnd": [
      {
        "type": "command",
        "command": "~/.fugue/hooks/session-audit.sh",
        "async": true
      }
    ]
  }
}
```

Each session will append entries to `~/.fugue/sessions.log`:

```
2026-05-01T14:23:15Z [SessionStart] model=claude-sonnet-4-6 provider=anthropic cwd=/home/alice/projects/myapp sessionId=01HVZK7F...
2026-05-01T14:24:57Z [SessionEnd] durationMs=102340 turnCount=5 cwd=/home/alice/projects/myapp
```

## Sync vs async

### Sync hooks (async: false)

Fugue **waits** for the hook to complete, up to `timeoutMs`. Useful for validation or context injection. Non-zero exit code is treated as a failure:

- If `onError: "log-continue"` → warning logged, session proceeds.
- If `onError: "log-block"` → session stops, error propagated.

**Phase 1 note:** `SessionStart` and `SessionEnd` hooks are informational and never block the session even with `log-block`; they can only log warnings or stop the session if configured that way.

**Phase 2 note:** `PreToolUse` hooks are typically sync (required for PreToolUse to check the result and decide whether to block). If a PreToolUse hook times out, Fugue logs the timeout and proceeds with the tool call unchanged.

**Phase 3 note:** `UserPromptSubmit` hooks are always sync (user is waiting for agent response; no fire-and-forget option). If a UserPromptSubmit hook times out, Fugue logs the timeout and proceeds with the original prompt unchanged.

### Async hooks (async: true)

Fugue **spawns** the hook and abandons it. The hook runs in the background; its exit code is never checked. Useful for non-critical notifications, logging, or long-running tasks. Timeouts are irrelevant for async hooks (ignored).

**Phase 2 note:** `PostToolUse` hooks are **always async** (fire-and-forget), even if you set `async: false` in the config. Fugue will treat `async: false` as `async: true` for PostToolUse entries and may log a warning.

**Recommendation:** Use async for side effects like sending Slack notifications, writing audit logs, or triggering webhooks. Use sync only when you need feedback (PreToolUse validation, UserPromptSubmit filtering) or must wait for the hook to complete (SessionStart setup).

## Failure modes

### Hook script doesn't exist

- `spawnHook` calls `Process.Start`, which throws if the executable is not found.
- Error is caught in `runLifecycle` and logged: `"[hooks] hook failed (...): No such file or directory"`.
- Behavior depends on `onError`:
  - `"log-continue"` → warning printed, session proceeds.
  - `"log-block"` → exception re-raised, session stops.

### Hook times out

- Sync hook exceeds `timeoutMs` (per-hook or global default).
- Fugue calls `proc.Kill(entireProcessTree = true)`, waits for cleanup, and returns exit code `-1` (sentinel for timeout).
- Logged: `"[hooks] hook {command} timed out after {N} ms (continuing)"` or error if `log-block`.
- For PreToolUse hooks that time out, the tool call proceeds unchanged (treated as `Proceed`).
- For UserPromptSubmit hooks that time out, the turn proceeds with the original prompt unchanged.

### Hook exits non-zero

- Exit code is any non-zero value (including `-1` from timeout, `-2` from exception).
- Logged: `"[hooks] hook {command} exited with code {N} (continuing)"` or error if `log-block`.

### PreToolUse hook outputs invalid JSON

- If your script outputs unparseable JSON to stdout, Fugue logs the parse error and proceeds unchanged.
- If you output `{"block": true}` without a `reason` field, the default reason is `"blocked by hook"`.

### UserPromptSubmit hook outputs invalid JSON

- If your script outputs unparseable JSON to stdout, Fugue logs the parse error and proceeds with the original prompt unchanged.
- If you output `{"block": true}` without a `reason` field, the default reason is `"blocked by hook"`.

### `~/.fugue/hooks.json` is missing

- No error. `load()` returns `defaultConfig` (empty hook lists), and `runLifecycle` is a no-op.

### `~/.fugue/hooks.json` is malformed JSON

- `JsonDocument.Parse` throws an exception in `load()`.
- Caught and logged: `"[hooks] failed to load ~/.fugue/hooks.json: {error}"`.
- `load()` returns `defaultConfig` and continues; no hooks run.

### Malformed hook definition (missing `command`, wrong `type`)

- Invalid hook definitions are silently skipped during parsing. Only hooks with `type: "command"` and a non-empty `command` field are yielded.
- No error is raised if a section is empty or all hooks are invalid.

## Limitations (Phase 3)

The following are planned for future phases and are **not yet supported**:

- **Context-provider injection** — hooks that output text to be prepended to the system prompt (Phase 4, absorbs #191).
- **HTTP webhooks** — webhook transport instead of local processes (#188, Phase 2+, separate issue).
- **Shell quoting in commands** — `command` is split on the first space only; complex args with spaces require a wrapper script.
- **Glob/regex matchers** — matcher patterns are reserved for future versions; only exact string match (`"*"` for all, or exact tool name like `"Bash"`) is supported in Phase 3.
- **All-input interception** — UserPromptSubmit only intercepts natural-language prompts. Slash commands, macros, and shell shortcuts bypass hooks. Full input filtering may come in Phase 4+.

## Testing

To verify hooks work locally:

1. Create a simple test script that echoes the input payload:

```bash
#!/bin/bash
cat > /tmp/hook-test.sh << 'EOF'
#!/bin/bash
echo "[hook-test] received:" >&2
cat >&2
exit 0
EOF
chmod +x /tmp/hook-test.sh
```

2. Add to `~/.fugue/hooks.json`:

```json
{
  "hookTimeoutMs": 5000,
  "onError": "log-continue",
  "hooks": {
    "SessionStart": [
      {
        "type": "command",
        "command": "/tmp/hook-test.sh",
        "async": false
      }
    ]
  }
}
```

3. Run a Fugue session:

```bash
fugue
```

4. Check stderr for the hook output:

```
[hooks] /tmp/hook-test.sh stderr: [hook-test] received:
{"event":"SessionStart","version":1,"cwd":...}
```

If you see this output, hooks are working. If the command is missing, you'll see `[hooks] hook failed (...): No such file or directory`.

### Testing PreToolUse hooks

To test a PreToolUse hook that blocks or modifies args:

1. Create a test hook that always blocks:

```bash
cat > /tmp/pre-block-test.sh << 'EOF'
#!/bin/bash
echo '{"block": true, "reason": "test block"}'
exit 0
EOF
chmod +x /tmp/pre-block-test.sh
```

2. Add to `~/.fugue/hooks.json`:

```json
{
  "hooks": {
    "PreToolUse": [
      {
        "type": "command",
        "command": "/tmp/pre-block-test.sh",
        "async": false,
        "matcher": "Bash"
      }
    ]
  }
}
```

3. Run Fugue and ask it to run a Bash command:

```
You: run `echo hello`
```

4. The hook will block the Bash invocation and return:

```
[hook-blocked] test block
```

The agent will see this as the tool result and can retry with a different command, or explain to you that the command was blocked.

### Testing UserPromptSubmit hooks

To test a UserPromptSubmit hook that blocks or modifies prompts:

1. Create a test hook that appends context:

```bash
cat > /tmp/enrich-test.sh << 'EOF'
#!/bin/bash
read -r payload
prompt=$(echo "$payload" | jq -r '.prompt')
if echo "$prompt" | grep -qi "time"; then
  current=$(date)
  printf '{"append": "\nCurrent time: %s"}\n' "$current"
else
  echo '{}'
fi
exit 0
EOF
chmod +x /tmp/enrich-test.sh
```

2. Add to `~/.fugue/hooks.json`:

```json
{
  "hooks": {
    "UserPromptSubmit": [
      {
        "type": "command",
        "command": "/tmp/enrich-test.sh",
        "async": false
      }
    ]
  }
}
```

3. Run Fugue and type a prompt mentioning time:

```
You: what's the time?
```

4. The hook intercepts the prompt, appends the current time, and the agent receives the enriched prompt. Check stderr or logs to see hook execution details.

If you want to test a blocking hook:

```bash
cat > /tmp/block-test.sh << 'EOF'
#!/bin/bash
read -r payload
prompt=$(echo "$payload" | jq -r '.prompt')
if echo "$prompt" | grep -qi "delete"; then
  echo '{"block": true, "reason": "prompts about deletion require human review"}'
else
  echo '{}'
fi
exit 0
EOF
chmod +x /tmp/block-test.sh
```

When the user types a prompt containing "delete", the hook blocks it:

```
You: delete all files matching *.tmp
⊘ prompt blocked by hook: prompts about deletion require human review
```

The user can revise and resubmit.
