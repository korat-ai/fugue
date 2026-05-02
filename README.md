# Fugue

> _A fugue in F# for your terminal._
> _Many voices, one console._

A lean CLI agent. Chat with AI agents (Anthropic / OpenAI / Ollama) in a TUI,
with the standard tool set (Read / Write / Edit / Bash / Glob / Grep).
Built on F# + Microsoft Agent Framework + Spectre.Console.

## Two binaries, two runtimes

Fugue ships as **two distributions** with deliberately different tradeoffs:

| Binary             | Runtime           | Cold start | Use case                              |
|--------------------|-------------------|-----------:|---------------------------------------|
| `fugue-aot`   | **Native AOT**    | ~40 ms     | CI / scripting / pipes / `--print`    |
| `fugue`            | **JIT + ReadyToRun** | ~150 ms    | Interactive REPL / TUI                |

**Why split:**

- **Headless** (`fugue --print "prompt"`, or pipe input via `cat file \| fugue …`)
  needs fast cold start on every invocation. Native AOT, single-file, no JIT
  warmup. Strict trim-friendly code: no reflection beyond what `System.Text.Json`
  source-gen and `Microsoft.Agents.AI` already need.
- **Interactive** runs once and stays alive for hours — JIT warmup is paid
  exactly once. Freed from AOT/trim constraints, the REPL can use reflection
  freely (dynamic MCP loading, `JsonSerializer` reflection mode, plugin
  discovery, full Spectre.Console feature surface).

**What this means for contributors:** the `every PR must AOT-publish-clean`
rule applies **only to `Fugue.Cli.Aot`** and its transitive closure
(`Fugue.Core` + `Fugue.Tools` + `Fugue.Agent`). Code in `Fugue.Cli` (REPL)
and `Fugue.Surface` (TUI primitives) is JIT-only — write idiomatic F#
without trim ceremony.

The headless trigger lives in one place (`Program.fs`):

```fsharp
let isHeadless (argv: string[]) : bool =
    argv |> Array.exists (fun a -> a = "--print" || a = "-p")
    || Console.IsInputRedirected
```

## Status

MVP shipped. AOT/JIT split adopted 2026-05-02 (see [§ Two binaries](#two-binaries-two-runtimes) above).

## Quickstart

```sh
brew install fugue                     # not yet
ANTHROPIC_API_KEY=sk-ant-... fugue
```

## Build from source

```sh
# Tests + interactive (JIT, fast iteration)
dotnet build
dotnet test
dotnet run --project src/Fugue.Cli

# Headless single-file native binary (Native AOT)
dotnet publish src/Fugue.Cli.Aot -c Release -r osx-arm64
./src/Fugue.Cli.Aot/bin/Release/net10.0/osx-arm64/publish/fugue-aot --print "hello"

# Interactive distribution (ReadyToRun, no AOT)
dotnet publish src/Fugue.Cli -c Release -r osx-arm64
./src/Fugue.Cli/bin/Release/net10.0/osx-arm64/publish/fugue
```
