# Sessions

A session is a conversation thread with the agent. Each Fugue run creates a fresh session on startup. The agent automatically saves all turns to JSONL (JSON Lines) format, enabling session restore and offline analysis. Phase 2 (#899) adds persistent storage and load/resume commands. Phase 3 adds full-text search across all stored sessions via SQLite FTS5, plus `/sessions` listing commands and offline `reindex` support.

## JSONL file location

Session JSONL files are stored at:
```
~/.fugue/sessions/<encoded-cwd>/<ulid>.jsonl
```

The `<encoded-cwd>` segment encodes the working directory where Fugue ran. This allows Fugue to maintain per-project session history without collisions.

### Path encoding rules

When encoding a filesystem path as a directory name:

1. Trim trailing `/` or `\`
2. Replace every `/` and `\` with `-`
3. Replace `:` (Windows drive separator) with `_`

Leading `-` characters (from absolute POSIX paths like `/Users/roman`) are intentional and preserved.

The original `cwd` is also stored in the `SessionStart` JSONL record, so it can be recovered if needed.

### Examples

| Original cwd | Encoded name |
|---|---|
| `/home/alice/projects/myapp` | `-home-alice-projects-myapp` |
| `C:\Users\Bob\repos\service` | `C_-Users-Bob-repos-service` |
| `/opt/fugue/.hidden/test` | `-opt-fugue-.hidden-test` |

Each encoded directory can contain multiple JSONL files (one per session), named by ULID:
```
~/.fugue/sessions/-home-alice-projects-myapp/
  01HVZK7F4X0000000000000001.jsonl
  01HVZK7F4X0000000000000002.jsonl
```

The ULID is a 26-character Crockford Base32 identifier generated per session. ULIDs are lexicographically sortable by timestamp — files in the directory naturally sort by session creation time.

## JSONL record types

Each line in a session JSONL file is a single JSON object representing one event. The file is written by `SessionPersistence.appendRecord` on each turn.

| Type | JSON field | Contents | Notes |
|---|---|---|---|
| **SessionStart** | `type: "session_start"` | `ts` (ISO 8601), `cwd` (original working dir), `model` (LLM model name), `provider` (e.g. `"anthropic"`) | Written once at session creation. Stores context. |
| **UserTurn** | `type: "user_turn"` | `ts`, `content` (user's prompt text) | One per user input. |
| **AssistantTurn** | `type: "assistant_turn"` | `ts`, `content` (assistant response text) | One per agent response. Phase 3: `content` is now captured and indexed. |
| **ToolCall** | `type: "tool_call"` | `ts`, `tool` (tool name), `args` (JSON stringified), `call_id` (unique ID) | One per tool invocation. |
| **ToolResult** | `type: "tool_result"` | `ts`, `call_id` (matches ToolCall), `output`, `is_error` (bool) | One per tool completion. |
| **SessionEnd** | `type: "session_end"` | `ts`, `turn_count` (int), `duration_ms` (int) | Written once on `/session offload` or normal exit. |

Example JSONL excerpt:
```json
{"type":"session_start","ts":"2026-05-01T10:00:00Z","cwd":"/home/alice/project","model":"claude-sonnet-4-6","provider":"anthropic"}
{"type":"user_turn","ts":"2026-05-01T10:00:05Z","content":"Read src/main.rs"}
{"type":"tool_call","ts":"2026-05-01T10:00:06Z","tool":"Read","args":"{\"path\":\"src/main.rs\"}","call_id":"call_001"}
{"type":"tool_result","ts":"2026-05-01T10:00:07Z","call_id":"call_001","output":"fn main() { ... }","is_error":false}
{"type":"assistant_turn","ts":"2026-05-01T10:00:08Z","content":"The main function is the entry point..."}
{"type":"session_end","ts":"2026-05-01T10:05:00Z","turn_count":1,"duration_ms":295000}
```

## `/session offload`

Manually flush the current session to JSONL and prepare for a new one.

**Steps:**

1. Write a `SessionEnd` record with the current timestamp, `turnCount`, and `0` as duration (not yet computed).
2. Flush the JSONL file to disk.
3. Print the session ID and file path to the user.
4. Dispose the old session object and create a new empty session.
5. Inject a synthetic system note so the agent acknowledges the context switch:
   ```
   Previous context offloaded to session <id>. Use GetConversation to inspect history if needed.
   ```
6. Return to the ready prompt.

**Example interaction:**

```
> /session offload
Session saved: 01HVZK7F4X0000000000000001
File: /home/alice/.fugue/sessions/-home-alice-projects-myapp/01HVZK7F4X0000000000000001.jsonl

```

The session history is now on disk. A new session is ready for new input. Tooling can later read the JSONL file for analysis or replay.

## `/session resume <id>`

Load a previously offloaded session back into memory.

**Parameters:**

- `<id>` — ULID (26-character hex/base32 string, e.g., `01HVZK7F4X0000000000000001`)

**Steps:**

1. Look up the JSONL file by ULID in the current working directory's session subdirectory.
   - Encoding uses the same rules as on offload: `~/.fugue/sessions/<encoded-cwd>/<id>.jsonl`.
2. Read all records from the file using `SessionPersistence.readRecords`.
   - Malformed or unknown lines are silently skipped (returns a list of `SessionRecord option`).
3. Reconstruct a `List<ChatMessage>`:
   - `UserTurn` → `ChatMessage(User, content)`
   - `AssistantTurn` → `ChatMessage(Assistant, content)`
   - `ToolCall` → `ChatMessage(Assistant, "[tool_call:ToolName call_id:…] args-json")` (encoded as text to avoid AOT issues; see **Tool-call replay** below)
   - `ToolResult` → `ChatMessage(User, "[tool_result:call-id] [error] output-text")` (error tag present if `is_error: true`)
   - `SessionStart` / `SessionEnd` → skipped
4. Create a new session and load the reconstructed message list using `AgentSessionExtensions.SetInMemoryChatHistory`.
5. Print the number of messages loaded.

**Example interaction:**

```
> /session resume 01HVZK7F4X0000000000000001
Resumed session 01HVZK7F4X0000000000000001 — 7 messages loaded.

```

The conversation history is now in memory. The agent can continue, inspect via `GetConversation`, or start a new direction.

### Tool-call replay

Tool calls are encoded as **text prefixes** rather than native `FunctionCallContent` objects. This design avoids AOT trimming issues (`MakeGenericMethod`, reflection on `FunctionCallContent`).

On resume, tool calls appear as:
```
[tool_call:Read call_id:call_001] {"path":"src/main.rs"}
```

And results appear as:
```
[tool_result:call_001] fn main() { ... }
[tool_result:call_001] [error] File not found
```

The agent sees the full context but **will not re-invoke the tool**. The history is replayed for context; actual tool re-execution would require Phase 3+ design.

## Search index

Session content is indexed into SQLite FTS5 (full-text search) for fast cross-session discovery.

### Index location

```
~/.fugue/index.db
```

A single SQLite database shared across all projects. Tables:

- **`sessions`** — metadata: `id` (ULID), `cwd` (project path), `started_at` (ISO 8601), `ended_at` (optional), `turn_count` (int), `summary` (optional)
- **`session_fts`** — FTS5 virtual table for full-text search: `session_id` (UNINDEXED, not searchable), `content` (searchable text blob)

### Tokenization

The FTS5 table uses `unicode61 remove_diacritics 1`, which:
- Splits on whitespace and punctuation (standard Unicode rules)
- Normalizes diacritics (é → e, ñ → n) for loose matching
- Preserves hyphens and quotes as part of tokens (e.g., `foo-bar` is one token)

### When the index is updated

The index is updated **in batch at session end** (in the `finally` block of the REPL loop). Specifically:
1. After `SessionEnd` is written to JSONL
2. `SearchIndex.upsertSession` writes the session metadata row
3. `SearchIndex.upsertContent` inserts/replaces the FTS5 row with the aggregated session content

**Why batch, not real-time per turn?** Lower latency in interactive mode. The JSONL file remains the durable source-of-truth; if the database is corrupted or lost, `fugue reindex` rebuilds everything. Amortizing writes to the end of the session is a classic trade-off: simpler code, fewer I/O ops, acceptable recovery story.

### What gets indexed

The FTS5 content blob for each session includes:
- All `UserTurn` content (user prompts)
- All `AssistantTurn` content with non-empty text (agent responses)
- All `ToolCall` content as `<tool-name> <args-json>` (tool invocations and parameters)

Tool results (`ToolResult` records) are **not** indexed separately (trade-off for size; users typically search by intent or tool name, not by output).

### Recovery: `fugue reindex`

If the index database is corrupted, lost, or missing on a fresh install:

```bash
fugue reindex
```

Walks all `*.jsonl` files under `~/.fugue/sessions/` (excluding `*.ndjson` session summary files), and rebuilds both the `sessions` metadata table and the `session_fts` FTS5 table from scratch.

**Output:**
```
Reindexed 42 session(s).
```

Returns exit code 0 on success, 1 on error.

## Slash commands

### `/sessions`

List all sessions in the **current project** (current working directory).

**Output (tabular, with colors when supported):**

```
Sessions for /home/alice/projects/myapp (5)
ID                         Started                   Turns  Summary
01HVZK7F4X0000000000000001  2026-05-01T10:00:00Z       5    Read src/main.rs, fixed bug in parser
01HVZK7F4X0000000000000002  2026-05-01T11:15:00Z       2    Added new feature to config…
01HVZK7F4X0000000000000003  2026-04-30T16:30:00Z       1    Explored database schema
…
```

**Fields:**

- **ID** — ULID (truncated to 26 chars for display)
- **Started** — ISO 8601 timestamp of session creation
- **Turns** — number of user turns in the session
- **Summary** — (optional) 3-sentence human-readable summary, generated at session end

**Limit:** 20 most recent sessions (by `started_at` DESC).

### `/sessions all`

List **all sessions across all projects** in `~/.fugue/index.db`.

**Output (tabular):**

```
All sessions (142)
ID                         Started                   Turns  Project
01HVZK7F4X0000000000000001  2026-05-01T10:00:00Z       5    …/home/alice/projects/myapp
01HVZK7F4X0000000000000002  2026-05-01T11:15:00Z       2    …/home/alice/projects/service
01HVZK7F4X0000000000000003  2026-04-30T16:30:00Z       1    …/home/alice/personal/scripts
…
```

**Fields:**

- **ID** — ULID (truncated to 26 chars)
- **Started** — ISO 8601 timestamp
- **Turns** — turn count
- **Project** — encoded working directory (truncated, prefixed with `…` if longer than 40 chars)

**Limit:** 50 most recent sessions (by `started_at` DESC).

### `/search <query>`

Full-text search across all session content in the current project and others.

**Usage:**

```
/search regex
/search foo-bar
/search "exact phrase"
/search claude
```

**Key behavior:**

User input is automatically wrapped in **FTS5 phrase-query syntax** (`"..."`) to handle special characters safely:
- `/search foo-bar` becomes FTS5 query `"foo-bar"` → searches for the literal phrase "foo-bar", not "foo OR NOT bar"
- `/search "my quote"` becomes `"\"my quote\""` → embedded quotes are doubled per SQLite FTS5 spec
- Avoids `SqliteException` on regex metacharacters (hyphens, asterisks, quotes)

**Output (up to 10 results, ranked by BM25 relevance):**

```
Search results for «regex» (3)
01HVZK7F4X0000000000000001  2026-05-01T10:00:00Z  …/myapp
  «regex» pattern matched on line 42. Let me test with this: …
01HVZK7F4X0000000000000002  2026-05-01T11:15:00Z  …/service
  Compiled the «regex» successfully. Results: match groups are …
…
```

**Fields per hit:**

- **ID** — ULID (truncated to 26 chars)
- **Started** — timestamp (truncated to date+time)
- **Project** — cwd (truncated, right-aligned with `…` if needed)
- **Snippet** — highlighted excerpt from matching content («…» marks the match context)

## `fugue --reindex` CLI flag

Rebuild the FTS5 search index from scratch.

**When to use:**

1. **Corrupted or missing database** — if `~/.fugue/index.db` is corrupted, lost, or you installed Fugue on a new machine with existing JSONL files
2. **Fresh install with legacy sessions** — migrating from Phase 2 (pre-FTS5) to Phase 3+
3. **Rebuild after manual JSONL edits** — if you manually edited session files, rebuild the index

**How it differs from interactive `/search`:**

- **`/search`** — runs a live query against the current index (fast, in-REPL)
- **`fugue reindex`** — offline batch rebuild from JSONL files (slower, standalone command-line tool)

**Usage:**

```bash
fugue reindex
```

**Output:**

```
Reindexed 42 session(s).
```

Walks all `*.jsonl` files recursively under `~/.fugue/sessions/` and upserts their metadata and content into the index database. Returns 0 on success, 1 on hard failure.

## Failure modes

### JSONL file missing on resume

If the file is not found in the encoded cwd directory:

```
Session not found: 01HVZK7F4X0000000000000001
```

The active session is **not modified**. User can retry with a correct ULID or `cd` to the correct project directory.

### Malformed JSONL lines during read

Lines that fail JSON parsing or have an unknown `type` field are silently skipped by `SessionRecord.deserialize` (returns `None`). `SessionPersistence.readRecords` uses `List.choose` to filter these out.

**Result:** A partial session is loaded. If 8 of 10 records parse, the agent sees 8 messages. No error is raised.

### Concurrent Fugue processes in same project

Two Fugue instances running in the same cwd will both write to the same JSONL files. Writes from `File.AppendAllText` may interleave — records can be corrupted or mixed. This is an **accepted limitation in Phase 2** (same trade-off as `SessionSummary.save`).

**Mitigation:** Use `/session offload` to cleanly separate sessions before running concurrent instances.

### User offloads twice in a row

The first `/session offload` writes `SessionEnd`, resets history, and injects the orientation note. If `/session offload` is called again immediately (without new turns):

1. A second `SessionEnd` is written with `turn_count: 0` (no new user turns).
2. The session is cleared and reset again.
3. A new `SessionStart` begins.

The JSONL file now contains:
```
SessionStart → (no turns) → SessionEnd
SessionStart → SessionEnd
```

Both are valid and benign. `GetConversation` reflects the latest state (empty). Resume by ULID remains correct.

### Index database missing or corrupted

The `/search`, `/sessions`, and `/sessions all` commands return empty results and log no errors (best-effort, same policy as `SearchIndex.upsertSession`). Run `fugue reindex` to rebuild.

## Related features

### GetConversation tool (Phase 1)

The `GetConversation` tool reads the **in-memory** session history as JSON. Use this to review context, build summaries, or debug state during a session.

See `docs/tools.md` for full reference.

### SessionStart and SessionEnd hooks

Hooks can run custom scripts at session lifecycle events. See `docs/hooks.md` for configuration and examples.

Example: fire an async webhook when a session ends.

### Session listing and search (Phase 3)

- **`/sessions`** — list sessions in the current project
- **`/sessions all`** — list all sessions across projects
- **`/search <query>`** — full-text search across all session content
- **`fugue reindex`** — rebuild the FTS5 index from JSONL files

All ship together in Phase 3 as a cohesive feature for session discovery and cross-project context.

## Limitations

The following are **not yet supported**:

- **Cross-cwd resume** (`--all` flag) — Phase 4. Currently must `cd` to the original project directory to resume.
- **Tool-call re-invocation on resume** — Tool calls and results are replayed as text for context, but no live re-execution. Useful for context but not automation replay.
- **Atomic multi-record writes** — Phase 2 uses `File.AppendAllText`; each record is written individually. Phase 3 may batch for crash recovery.
- **Advanced FTS5 operators** — `/search` wraps user input in phrase syntax for safety; boolean operators (`AND`, `OR`, `NOT`), wildcards, and fuzzy matching are not exposed (users can run `sqlite3 ~/.fugue/index.db "SELECT …"` for advanced queries).

---

## See also

- `docs/tools.md` — `GetConversation` tool reference
- `docs/hooks.md` — `SessionStart` and `SessionEnd` hooks
- `CLAUDE.md` — project conventions

