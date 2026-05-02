# Fugue dev task runner.
# Install just: `brew install just`. Run `just` (with no args) to list targets.
# CI does NOT depend on this file — it shells out to `dotnet` directly so the
# project remains buildable without `just` installed.

set shell := ["bash", "-eu", "-o", "pipefail", "-c"]

# Default target — show available recipes.
default:
    @just --list

# Restore + build all projects in Release.
build:
    dotnet build -c Release

# Run the full xUnit test suite (assumes a recent build; use `just test-fresh`
# if you want a clean rebuild first).
test:
    dotnet test --no-build -c Release

# Clean build artefacts in test project, then build + test from scratch.
# Use this when stale DLLs are masking real failures.
test-fresh:
    dotnet clean tests/Fugue.Tests
    dotnet build -c Release
    dotnet test --no-build -c Release

# Run only one test class or pattern.
# Usage: `just test-only ApprovalMode`
test-only filter:
    dotnet test --no-build -c Release --filter "FullyQualifiedName~{{filter}}"

# AOT-publish the native single-file binary for the current host (osx-arm64
# is the default; override with TARGET=linux-x64 etc.).
publish target="osx-arm64":
    dotnet publish src/Fugue.Cli -c Release -r {{target}}

# Build + publish + run the binary. The fastest one-shot path from clean tree
# to interactive REPL — what you want during local UX iteration.
run target="osx-arm64": (publish target)
    ./src/Fugue.Cli/bin/Release/net10.0/{{target}}/publish/fugue

# Run the binary with an explicit approval mode (plan / default / auto-edit /
# yolo). Useful for testing the gate.
run-mode mode target="osx-arm64": (publish target)
    ./src/Fugue.Cli/bin/Release/net10.0/{{target}}/publish/fugue --mode {{mode}}

# Smoke check — print version and help (proves the AOT binary boots).
smoke target="osx-arm64": (publish target)
    ./src/Fugue.Cli/bin/Release/net10.0/{{target}}/publish/fugue --version
    ./src/Fugue.Cli/bin/Release/net10.0/{{target}}/publish/fugue --help

# Filter the publish log for IL trim/AOT warnings — these block release-grade
# builds even if the build itself succeeds.
aot-check target="osx-arm64":
    @dotnet publish src/Fugue.Cli -c Release -r {{target}} 2>&1 | grep -E "IL[0-9]+|Error|error" || echo "✓ AOT clean — 0 IL warnings"

# Show binary size + version for the most recent publish.
size target="osx-arm64":
    @ls -lh ./src/Fugue.Cli/bin/Release/net10.0/{{target}}/publish/fugue
    @./src/Fugue.Cli/bin/Release/net10.0/{{target}}/publish/fugue --version

# Pull live open issues from GitHub — used by orchestrator/planning workflows.
issues:
    gh issue list -R korat-ai/fugue --state open --limit 100 \
      --json number,title,labels \
      --jq '.[] | "#\(.number) [\(.labels|map(.name)|join(","))] \(.title)"'

# Headless / non-interactive sanity test against a one-shot prompt.
# Usage: `just print "what is 2+2?"`
print prompt target="osx-arm64": (publish target)
    ./src/Fugue.Cli/bin/Release/net10.0/{{target}}/publish/fugue --print "{{prompt}}"
