module Fugue.Tests.ConfigDisplayNameTests

open Xunit
open FsUnit.Xunit
open Fugue.Core.Config

// ── exact matches ────────────────────────────────────────────────────────────

[<Fact>]
let ``modelDisplayName exact match claude-opus-4-7`` () =
    modelDisplayName "claude-opus-4-7" |> should equal "Claude Opus 4.7"

[<Fact>]
let ``modelDisplayName exact match claude-sonnet-4-6`` () =
    modelDisplayName "claude-sonnet-4-6" |> should equal "Claude Sonnet 4.6"

[<Fact>]
let ``modelDisplayName exact match gpt-4o`` () =
    modelDisplayName "gpt-4o" |> should equal "GPT-4o"

[<Fact>]
let ``modelDisplayName exact match gpt-3.5`` () =
    modelDisplayName "gpt-3.5" |> should equal "GPT-3.5"

// ── prefix / suffix matching ─────────────────────────────────────────────────

[<Fact>]
let ``modelDisplayName prefix match claude-opus-4-7 with date suffix`` () =
    modelDisplayName "claude-opus-4-7-20250514" |> should equal "Claude Opus 4.7"

[<Fact>]
let ``modelDisplayName prefix match claude-3-5-sonnet with detail suffix`` () =
    modelDisplayName "claude-3-5-sonnet-20241022" |> should equal "Claude 3.5 Sonnet"

[<Fact>]
let ``modelDisplayName prefix match gpt-4-turbo with suffix`` () =
    modelDisplayName "gpt-4-turbo-preview" |> should equal "GPT-4 Turbo"

// ── case insensitivity ────────────────────────────────────────────────────────

[<Fact>]
let ``modelDisplayName case insensitive upper-case claude-opus-4-7`` () =
    modelDisplayName "CLAUDE-OPUS-4-7" |> should equal "Claude Opus 4.7"

[<Fact>]
let ``modelDisplayName case insensitive mixed-case gpt-4o`` () =
    modelDisplayName "GPT-4O" |> should equal "GPT-4o"

// ── unknown model fallback ───────────────────────────────────────────────────

[<Fact>]
let ``modelDisplayName unknown model returns modelId unchanged`` () =
    modelDisplayName "unknown-model-xyz" |> should equal "unknown-model-xyz"

[<Fact>]
let ``modelDisplayName empty string returns empty string`` () =
    modelDisplayName "" |> should equal ""

// ── prefix ordering invariant ─────────────────────────────────────────────────
// Verify that longer (more specific) prefixes win over shorter ones.
// e.g. "gpt-4o" must be found before "gpt-4" in the list; "gpt-4-turbo" before "gpt-4".

[<Fact>]
let ``modelDisplayName gpt-4o-mini resolved to GPT-4o not GPT-4`` () =
    // "gpt-4o" prefix must precede "gpt-4" in the table
    modelDisplayName "gpt-4o-mini" |> should equal "GPT-4o"

[<Fact>]
let ``modelDisplayName gpt-4-turbo-2024 resolved to GPT-4 Turbo not GPT-4`` () =
    // "gpt-4-turbo" prefix must precede "gpt-4" in the table
    modelDisplayName "gpt-4-turbo-2024" |> should equal "GPT-4 Turbo"

[<Fact>]
let ``modelDisplayName claude-haiku-4-5 resolved to Claude Haiku 4.5 not Claude Haiku 4`` () =
    // "claude-haiku-4-5" prefix must precede "claude-haiku-4" in the table
    modelDisplayName "claude-haiku-4-5-20251014" |> should equal "Claude Haiku 4.5"

[<Fact>]
let ``modelDisplayName claude-3-7-sonnet resolved correctly and not shadowed by claude-3-5-sonnet`` () =
    modelDisplayName "claude-3-7-sonnet-20250219" |> should equal "Claude 3.7 Sonnet"

[<Fact>]
let ``modelDisplayName deepseek-coder resolved to DeepSeek Coder not DeepSeek`` () =
    // "deepseek-coder" prefix must precede "deepseek" in the table
    modelDisplayName "deepseek-coder-v2" |> should equal "DeepSeek Coder"

[<Fact>]
let ``modelDisplayName qwen2.5-coder resolved to Qwen 2.5 Coder not Qwen 2.5`` () =
    // "qwen2.5-coder" prefix must precede "qwen2.5" in the table
    modelDisplayName "qwen2.5-coder-32b" |> should equal "Qwen 2.5 Coder"

[<Fact>]
let ``modelDisplayName gemma3 resolved correctly not confused with gemma2`` () =
    modelDisplayName "gemma3:27b" |> should equal "Gemma 3"

[<Fact>]
let ``modelDisplayName o3-mini resolved to o3 display name`` () =
    // "o3" prefix must precede "o1" in the table and match "o3-mini"
    modelDisplayName "o3-mini" |> should equal "o3"
