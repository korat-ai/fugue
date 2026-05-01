# Hooks

Hooks are user-defined scripts that run at key lifecycle points in a Fugue session. They receive structured JSON context via stdin and can log, notify, or integrate with external systems. Phase 1 ships `SessionStart` and `SessionEnd` hooks — more lifecycle events (`PreToolUse`, `PostToolUse`, `UserPromptSubmit`, context-provider injection) are planned for Phase 2–4.

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
    ]
  }
}
```

### Top-level fields

| Field | Type | Default | Notes |
|-------|------|---------|-------|
| `hookTimeoutMs` | int | 5000 | Global timeout (ms) for synchronous hooks. Overridden per-hook via `timeoutMs`. |
| `onError` | string | `"log-continue"` | How to handle hook failures: `"log-continue"` (warn, keep going) or `"log-block"` (fail the session). |
| `hooks` | object | `{}` | Container for per-event hook arrays. Possible keys: `SessionStart`, `SessionEnd`. |

### Hook definition (per hook in an array)

| Field | Type | Default | Notes |
|-------|------|---------|-------|
| `type` | string | — | Must be `"command"`. |
| `command` | string | — | Path or command to execute. Supports `~/` prefix. No shell quoting; space splits exe from args (first space only). |
| `async` | bool | false | If true, fire-and-forget; if false, Fugue waits up to `timeoutMs`. |
| `timeoutMs` | int | (global) | Per-hook override for `hookTimeoutMs`. Ignored for async hooks. |

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

## Writing a hook script

Hook scripts are normal executables (shell, Python, Go, etc.). They read JSON from stdin and process it. Exit code `0` signals success; non-zero signals failure (handled per `onError`).

### Example: Session audit log

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

### Async hooks (async: true)

Fugue **spawns** the hook and abandons it. The hook runs in the background; its exit code is never checked. Useful for non-critical notifications, logging, or long-running tasks. Timeouts are irrelevant for async hooks (ignored).

**Recommendation:** Use async for side effects like sending Slack notifications, writing audit logs, or triggering webhooks. Use sync only when you need feedback before proceeding.

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

### Hook exits non-zero

- Exit code is any non-zero value (including `-1` from timeout, `-2` from exception).
- Logged: `"[hooks] hook {command} exited with code {N} (continuing)"` or error if `log-block`.

### `~/.fugue/hooks.json` is missing

- No error. `load()` returns `defaultConfig` (empty hook lists), and `runLifecycle` is a no-op.

### `~/.fugue/hooks.json` is malformed JSON

- `JsonDocument.Parse` throws an exception in `load()`.
- Caught and logged: `"[hooks] failed to load ~/.fugue/hooks.json: {error}"`.
- `load()` returns `defaultConfig` and continues; no hooks run.

### Malformed hook definition (missing `command`, wrong `type`)

- Invalid hook definitions are silently skipped during parsing. Only hooks with `type: "command"` and a non-empty `command` field are yielded.
- No error is raised if a section is empty or all hooks are invalid.

## Limitations (Phase 1)

The following are planned for future phases and are **not yet supported**:

- **PreToolUse** hooks — intercept and block/modify tool invocations (Phase 2).
- **PostToolUse** hooks — fire after tool completion, e.g., for formatting (Phase 2).
- **UserPromptSubmit** hooks — modify or block user prompts before agent processing (Phase 3).
- **Context-provider injection** — hooks that output text to be prepended to the system prompt (Phase 4, absorbs #191).
- **HTTP webhooks** — webhook transport instead of local processes (#188, Phase 2+, separate issue).
- **Shell quoting in commands** — `command` is split on the first space only; complex args with spaces require a wrapper script.
- **Hook result protocol** — hooks cannot return structured feedback (e.g., `block: true`, `modified_args`) in Phase 1. Output is logged if present on stderr; stdout is ignored.

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
