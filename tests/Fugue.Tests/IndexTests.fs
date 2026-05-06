module Fugue.Tests.IndexTests

open System
open System.IO
open Xunit
open FsUnit.Xunit
open Fugue.Core.Index
open Fugue.Agent.Indexer

// ---------------------------------------------------------------------------
// Helper: each test gets its own temp dir via FUGUE_INDEX_DIR env override
// ---------------------------------------------------------------------------

let private withTmpDir (f: string -> unit) =
    let tmp = Path.Combine(Path.GetTempPath(), "FugueIndexTests_" + Path.GetRandomFileName())
    Directory.CreateDirectory tmp |> ignore
    let prev = Environment.GetEnvironmentVariable "FUGUE_INDEX_DIR"
    try
        Environment.SetEnvironmentVariable("FUGUE_INDEX_DIR", tmp)
        f tmp
    finally
        Environment.SetEnvironmentVariable("FUGUE_INDEX_DIR", prev)
        try Directory.Delete(tmp, true) with _ -> ()

// ---------------------------------------------------------------------------
// 1. chunkFile single window
// ---------------------------------------------------------------------------

[<Fact>]
let ``chunkFile single window — file with 35 lines yields 1 chunk (1, 35)`` () =
    let tmp = Path.GetTempFileName()
    try
        let lines = Array.init 35 (fun i -> $"line {i+1}")
        File.WriteAllLines(tmp, lines)
        let chunks = chunkFile 40 10 tmp |> Seq.toList
        chunks |> List.length |> should equal 1
        let (s, e, _) = chunks.[0]
        s |> should equal 1
        e |> should equal 35
    finally
        try File.Delete tmp with _ -> ()

// ---------------------------------------------------------------------------
// 2. chunkFile multi-window
// ---------------------------------------------------------------------------

[<Fact>]
let ``chunkFile multi-window — 90 lines, chunkLines=40, overlap=10 yields chunks with overlapping ranges`` () =
    let tmp = Path.GetTempFileName()
    try
        let lines = Array.init 90 (fun i -> $"line {i+1}")
        File.WriteAllLines(tmp, lines)
        // step = 40 - 10 = 30; windows start at 0, 30, 60 (0-based)
        // chunk 1: lines 1-40, chunk 2: lines 31-70, chunk 3: lines 61-90
        let chunks = chunkFile 40 10 tmp |> Seq.toList
        chunks |> List.length |> should equal 3
        let (s1, e1, _) = chunks.[0]
        let (s2, e2, _) = chunks.[1]
        let (s3, e3, _) = chunks.[2]
        s1 |> should equal 1
        e1 |> should equal 40
        s2 |> should equal 31
        e2 |> should equal 70
        s3 |> should equal 61
        e3 |> should equal 90
    finally
        try File.Delete tmp with _ -> ()

// ---------------------------------------------------------------------------
// 3. chunkFile exact boundary
// ---------------------------------------------------------------------------

[<Fact>]
let ``chunkFile exact boundary — 40 lines yields exactly 1 chunk, no spurious second`` () =
    let tmp = Path.GetTempFileName()
    try
        let lines = Array.init 40 (fun i -> $"line {i+1}")
        File.WriteAllLines(tmp, lines)
        let chunks = chunkFile 40 10 tmp |> Seq.toList
        chunks |> List.length |> should equal 1
        let (s, e, _) = chunks.[0]
        s |> should equal 1
        e |> should equal 40
    finally
        try File.Delete tmp with _ -> ()

// ---------------------------------------------------------------------------
// 4. cosineSimilarity
// ---------------------------------------------------------------------------

let private approxEq (expected: float32) (actual: float32) =
    MathF.Abs(actual - expected) < 1e-5f

[<Fact>]
let ``cosineSimilarity — identical vectors return 1.0; orthogonal return 0.0`` () =
    let a = [| 1.0f; 0.0f; 0.0f |]
    let b = [| 0.0f; 1.0f; 0.0f |]
    let c = [| 1.0f; 0.0f; 0.0f |]
    cosineSimilarity a b |> approxEq 0.0f |> should equal true
    cosineSimilarity a c |> approxEq 1.0f |> should equal true

// ---------------------------------------------------------------------------
// 5. NDJSON round-trip
// ---------------------------------------------------------------------------

[<Fact>]
let ``ndjson round-trip — appendChunk x3, loadChunks returns 3 with field equality`` () =
    withTmpDir (fun tmp ->
        let path = Path.Combine(tmp, "test-hash", "chunks.ndjson")
        let chunks =
            [ { FilePath = "src/foo.fs"; ChunkIdx = 0; StartLine = 1; EndLine = 10
                Text = "hello world"; Vector = [| 0.1f; 0.2f; 0.3f |]; ModelId = "m1"; IndexedAt = "2026-05-01T00:00:00+00:00" }
              { FilePath = "src/bar.fs"; ChunkIdx = 0; StartLine = 1; EndLine = 5
                Text = "bar content"; Vector = [| 0.4f; 0.5f; 0.6f |]; ModelId = "m1"; IndexedAt = "2026-05-01T00:00:01+00:00" }
              { FilePath = "src/baz.fs"; ChunkIdx = 1; StartLine = 11; EndLine = 20
                Text = "baz content"; Vector = [| 0.7f; 0.8f; 0.9f |]; ModelId = "m1"; IndexedAt = "2026-05-01T00:00:02+00:00" } ]
        for c in chunks do
            appendChunk path c
        let loaded = loadChunks path
        loaded |> List.length |> should equal 3
        loaded.[0].FilePath   |> should equal "src/foo.fs"
        loaded.[0].Vector.[1] |> approxEq 0.2f |> should equal true
        loaded.[1].ChunkIdx   |> should equal 0
        loaded.[2].EndLine    |> should equal 20
    )

// ---------------------------------------------------------------------------
// 6. chunksPath honors FUGUE_INDEX_DIR env override
// ---------------------------------------------------------------------------

[<Fact>]
let ``chunksPath honors FUGUE_INDEX_DIR env override`` () =
    withTmpDir (fun tmp ->
        let path = chunksPath "/my/project"
        path |> should startWith tmp
        path |> should endWith "chunks.ndjson"
    )

// ---------------------------------------------------------------------------
// 7. isExcluded
// ---------------------------------------------------------------------------

[<Fact>]
let ``isExcluded — *.min.js excluded, src/main.fs not excluded`` () =
    let globs = [ "**/*.min.js"; "**/node_modules/**" ]
    isExcluded globs "dist/app.min.js" |> should equal true
    isExcluded globs "node_modules/react/index.js" |> should equal true
    isExcluded globs "src/main.fs" |> should equal false
    isExcluded globs "src/Repl.fs" |> should equal false

// ---------------------------------------------------------------------------
// 8. parseOllamaEmbedResponse / parseLmStudioEmbedResponse
// ---------------------------------------------------------------------------

[<Fact>]
let ``parseOllamaEmbedResponse on known JSON returns expected float32 array`` () =
    let json = """{"embedding":[0.1,0.2,0.3]}"""
    let result = parseOllamaEmbedResponse json
    result |> should not' (equal None)
    let arr = result.Value
    arr.Length |> should equal 3
    arr.[0] |> approxEq 0.1f |> should equal true
    arr.[1] |> approxEq 0.2f |> should equal true
    arr.[2] |> approxEq 0.3f |> should equal true

[<Fact>]
let ``parseLmStudioEmbedResponse on known JSON returns expected float32 array`` () =
    let json = """{"data":[{"embedding":[0.5,0.6,0.7]}]}"""
    let result = parseLmStudioEmbedResponse json
    result |> should not' (equal None)
    let arr = result.Value
    arr.Length |> should equal 3
    arr.[0] |> approxEq 0.5f |> should equal true
    arr.[1] |> approxEq 0.6f |> should equal true
    arr.[2] |> approxEq 0.7f |> should equal true
