# Fugue

> _A fugue in F# for your terminal._
> _Many voices, one console._

A lean, native-AOT CLI agent. Chat with AI agents (Anthropic / OpenAI / Ollama) in a TUI, with the standard tool set (Read / Write / Edit / Bash / Glob / Grep). Built on F# + Microsoft Agent Framework + Terminal.Gui v2.

## Status

Pre-alpha — implementation in progress. See `docs/superpowers/specs/2026-04-29-fugue-design.md` and `docs/superpowers/plans/2026-04-29-fugue.md`.

## Quickstart (planned)

```sh
brew install fugue                     # not yet
ANTHROPIC_API_KEY=sk-ant-... fugue
```

## Build from source

```sh
dotnet build
dotnet test
dotnet publish src/Fugue.Cli -c Release -r osx-arm64 /p:PublishAot=true
./src/Fugue.Cli/bin/Release/net10.0/osx-arm64/publish/fugue
```
