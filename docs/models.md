# Model Discovery and Selection

When you run Fugue, it automatically discovers the available models for your active provider at session start. The `/model set` command lets you switch between them using interactive Tab completion. This document describes how model discovery works, what data sources are used per provider, and caching behavior.

## Overview

Model discovery is implemented by the `ModelDiscovery` module (`src/Fugue.Agent/ModelDiscovery.fs`) and integrated with the ReadLine input handler (`src/Fugue.Cli/ReadLine.fs`). The REPL maintains a cached list of available models in the current session. On session start, the cache is populated in the background; after a `/model set` switch (or `/model` picker), the cache is refreshed asynchronously.

## The `/model set` Command

```
/model set <prefix>
```

Use this to switch to a model on the current provider. When you type a prefix (e.g., `/model set claude-son`), the inline ghost suggestion shows the first match. Press **Tab to cycle** through all models whose names begin with your prefix.

### Examples

**Anthropic provider:**
```
> /model set claude-op
  (ghost suggestion shows: claude-opus-4-7)

> [press Tab]
  1. claude-opus-4-7
  2. [Tab again] cycles to next match (if any)

> [type more or Tab to select]
/model set claude-opus-4-7 → [Enter]
```

**OpenAI-compatible or Ollama provider:**
```
> /model set gpt-4
  (ghost suggestion shows first match from fetched models)

> [Tab cycles through matching models from the provider's API]
```

## Model Data Sources by Provider

| Provider | Source | Endpoint | Caching | Notes |
|----------|--------|----------|---------|-------|
| **Anthropic** | Hardcoded list | (none) | None | 3 models: `claude-opus-4-7`, `claude-sonnet-4-6`, `claude-haiku-4-5-20251001`. No HTTP — always available. |
| **OpenAI-compatible (LM Studio, etc.)** | HTTP GET | `{baseUrl}/v1/models` | 1500ms timeout | Fetched at session start and after `/model set`. Returns `{"data": [{"id": "model-name"}, ...]}`. |
| **Ollama** | HTTP GET | `{endpoint}/api/tags` | 1500ms timeout | Fetched at session start and after `/model set`. Returns `{"models": [{"name": "model-name"}, ...]}`. |

### Anthropic (Hardcoded)

Anthropic does not expose a public `/v1/models` endpoint without account-scoped authentication headers. To avoid complexity and external dependencies, Fugue ships with a hardcoded list of three known Claude models:

- `claude-opus-4-7`
- `claude-sonnet-4-6`
- `claude-haiku-4-5-20251001`

This list is updated manually when new Claude releases ship. To update it, edit the `anthropicKnownModels` list in `src/Fugue.Agent/ModelDiscovery.fs`.

### OpenAI-Compatible (LM Studio, Vllm, etc.)

For providers that expose the OpenAI API (e.g., LM Studio, Vllm, local Ollama OpenAI-compat layer):

1. Fugue fetches `GET {baseUrl}/v1/models` (where `baseUrl` comes from your `~/.fugue/config.json` or CLI flag).
2. It parses the JSON response: looks for `["data"]` (array) and extracts each `id` field.
3. On failure (network timeout, wrong baseUrl, empty response, JSON parse error), returns empty list.

**Example response:**
```json
{
  "data": [
    { "id": "gpt-4-turbo", "object": "model", ... },
    { "id": "gpt-3.5-turbo", "object": "model", ... }
  ]
}
```

### Ollama

For local Ollama instances:

1. Fugue fetches `GET {endpoint}/api/tags` (where `endpoint` is the Ollama connection URL, e.g., `http://localhost:11434`).
2. It parses the JSON response: looks for `["models"]` (array) and extracts each `name` field.
3. On failure (network timeout, wrong endpoint, empty response, JSON parse error), returns empty list.

**Example response:**
```json
{
  "models": [
    { "name": "llama2:7b", "modified_at": "...", ... },
    { "name": "mistral:7b", "modified_at": "...", ... }
  ]
}
```

## Caching and Refresh Behavior

When you start a Fugue session:

1. **Immediate:** The REPL displays the prompt. The provider's model list is fetched in the background (non-blocking).
2. **Fetch timeout:** HTTP requests time out after 1500ms to avoid blocking the REPL.
3. **Background population:** The `cachedModels` reference is populated asynchronously. If you press Tab before the fetch completes, the first Tab press shows nothing; subsequent Tab presses will work once the fetch finishes.
4. **After `/model set`:** The cache is refreshed in the background. You can immediately use another `/model set` while the refresh is in flight.

### Session Lifetime Caching

The cached model list persists for the entire session. It is **not** auto-refreshed if:

- You install a new model in Ollama or LM Studio (you must `/new` to pick it up).
- The provider's model list changes (e.g., a remote provider adds/removes models mid-session).

To see newly installed models, run `/new` to start a fresh session.

### Provider Switching Gotcha

If you use `/model set` to switch to a different provider (e.g., Anthropic → OpenAI), the cache is fetched for the **new** provider. However, inline ghost suggestions use only the prefix-match logic; they do not show a `[stale]` indicator if the previous fetch failed. See "What's Not Yet Implemented" below.

## Tab vs. Inline Suggestion

The inline **ghost suggestion** (shown as gray text during typing) shows the first alphabetical match for your prefix among cached models. It's a quick preview.

**Tab cycling** iterates through **all** matches in order, letting you pick any model with the same prefix. Each Tab press advances to the next match; the cursor position and text stay fixed — only the suggestion changes.

## Failure Modes

### Provider HTTP fails (timeout, network down, wrong baseUrl)

- **Anthropic:** No effect; hardcoded models are always available.
- **OpenAI-compat / Ollama:** The cache remains empty. Tab shows nothing. Ghost suggestions show nothing. You can still manually type `/model set <name>` and switch if you know the model name.
- **User-friendly message:** If you run `/model` (the interactive picker), Fugue displays:
  ```
  No models discovered. Use /model set <name> to switch manually.
  ```

### Provider returns an empty model list

Same as HTTP failure: Tab and suggestions show nothing. Manual entry still works.

### User presses Tab before fetch completes

The first Tab press returns nothing (empty suggestion list). Once the background fetch finishes, subsequent Tab presses work.

### Anthropic provider always works

Because it's hardcoded, Tab and suggestions always show the three known Claude models, regardless of network state.

## What's NOT Yet Implemented

These features are tracked as separate issues; they may land in future releases:

1. **Provider-type switching mid-session doesn't trigger refetch** (#520 or related)
   - If you change from `Anthropic` to `OpenAI` (via manual config edit, not via UI), the model cache still holds the old provider's models. Workaround: `/new`.

2. **No `[stale]` indicator on failed fetches**
   - If a fetch times out or fails, there's no visual indication. The cache is silently empty, and Tab shows nothing.

3. **No model capability metadata** (#520)
   - No context-window size, pricing, input/output token limits, or capabilities listed alongside model names.

4. **No fuzzy search** (prefix-only)
   - Tab and suggestions use strict prefix matching (case-insensitive). Searching for `gpt4` won't find `gpt-4-turbo`. Workaround: type the correct prefix.

5. **No model persistence across sessions** (Phase 3)
   - Fugue doesn't yet remember which model you used in a previous session. The model defaults to the config value on each new session.

## Configuration

Model discovery respects two config sources:

- **Provider config** (`~/.fugue/config.json`):
  ```json
  {
    "provider": "anthropic",
    "model": "claude-opus-4-7"
  }
  ```

- **CLI overrides**: `fugue --provider openai --baseUrl http://lm-studio:8000`

When you run `/model set`, Fugue writes the new model to the session's config override (in-memory, not persisted to disk unless you save manually). The new model is used for all subsequent turns in that session.

## See Also

- `/model` — Interactive model picker (no arguments).
- `/model suggest` — Ask the agent for a model recommendation.
- `docs/config.schema.json` — Provider configuration reference.
- `src/Fugue.Agent/ModelDiscovery.fs` — Implementation details.
- `src/Fugue.Cli/ReadLine.fs` — Tab-completion logic.
