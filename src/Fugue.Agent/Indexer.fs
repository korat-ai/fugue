module Fugue.Agent.Indexer

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open Fugue.Core.Index

// ---------------------------------------------------------------------------
// Provider DU
// ---------------------------------------------------------------------------

type EmbeddingProvider =
    | Ollama    of baseUrl: string
    | LmStudio  of baseUrl: string

// ---------------------------------------------------------------------------
// Response parsers (pure, testable)
// ---------------------------------------------------------------------------

/// Parse Ollama embedding response: {"embedding":[...]}
let parseOllamaEmbedResponse (json: string) : float32[] option =
    try
        match Option.ofObj (JsonNode.Parse json) with
        | None -> None
        | Some root ->
            match Option.ofObj root.["embedding"] with
            | None -> None
            | Some node ->
                match (node :> obj) with
                | :? JsonArray as arr ->
                    arr
                    |> Seq.cast<JsonNode | null>
                    |> Seq.choose Option.ofObj
                    |> Seq.map (fun n -> n.GetValue<float32>())
                    |> Array.ofSeq
                    |> Some
                | _ -> None
    with _ -> None

/// Parse LM Studio / OpenAI-compat embedding response: {"data":[{"embedding":[...]}]}
let parseLmStudioEmbedResponse (json: string) : float32[] option =
    try
        match Option.ofObj (JsonNode.Parse json) with
        | None -> None
        | Some root ->
            match Option.ofObj root.["data"] with
            | None -> None
            | Some dataNode ->
                match (dataNode :> obj) with
                | :? JsonArray as arr when arr.Count > 0 ->
                    match Option.ofObj arr.[0] with
                    | None -> None
                    | Some first ->
                        match Option.ofObj first.["embedding"] with
                        | None -> None
                        | Some embNode ->
                            match (embNode :> obj) with
                            | :? JsonArray as embArr ->
                                embArr
                                |> Seq.cast<JsonNode | null>
                                |> Seq.choose Option.ofObj
                                |> Seq.map (fun n -> n.GetValue<float32>())
                                |> Array.ofSeq
                                |> Some
                            | _ -> None
                | _ -> None
    with _ -> None

// ---------------------------------------------------------------------------
// HTTP embedding
// ---------------------------------------------------------------------------

/// POST text to embedding endpoint; returns None on any error.
let embedAsync (provider: EmbeddingProvider) (model: string) (http: HttpClient) (text: string) : Task<float32[] option> =
    task {
        try
            match provider with
            | Ollama baseUrl ->
                let url  = $"{baseUrl.TrimEnd '/'}/api/embeddings"
                let body = $"""{{ "model": "{model}", "prompt": {System.Text.Json.JsonSerializer.Serialize text} }}"""
                use content = new StringContent(body, Encoding.UTF8, "application/json")
                let! resp = http.PostAsync(url, content)
                if not resp.IsSuccessStatusCode then return None
                else
                    let! json = resp.Content.ReadAsStringAsync()
                    return parseOllamaEmbedResponse json
            | LmStudio baseUrl ->
                let url  = $"{baseUrl.TrimEnd '/'}/v1/embeddings"
                let body = $"""{{ "model": "{model}", "input": {System.Text.Json.JsonSerializer.Serialize text} }}"""
                use content = new StringContent(body, Encoding.UTF8, "application/json")
                let! resp = http.PostAsync(url, content)
                if not resp.IsSuccessStatusCode then return None
                else
                    let! json = resp.Content.ReadAsStringAsync()
                    return parseLmStudioEmbedResponse json
        with _ -> return None
    }

// ---------------------------------------------------------------------------
// File chunking
// ---------------------------------------------------------------------------

/// Split a file into overlapping line windows.
/// Yields (startLine, endLine, text) tuples with 1-based line numbers.
/// Slides by (chunkLines - overlapLines) per step.
let chunkFile (chunkLines: int) (overlapLines: int) (path: string) : (int * int * string) seq =
    seq {
        try
            let lines = File.ReadAllLines path
            let total = lines.Length
            if total = 0 then ()
            else
                let step = max 1 (chunkLines - overlapLines)
                let mutable start = 0  // 0-based index
                let mutable cont = true
                while cont do
                    let endIdx = min (start + chunkLines - 1) (total - 1)  // 0-based inclusive
                    let chunk  = lines.[start..endIdx] |> String.concat "\n"
                    yield (start + 1, endIdx + 1, chunk)  // 1-based
                    if endIdx >= total - 1 then
                        cont <- false
                    else
                        start <- start + step
        with _ -> ()
    }

// ---------------------------------------------------------------------------
// Full scan
// ---------------------------------------------------------------------------

let private buildProviderForConfig (cfg: IndexConfig) : EmbeddingProvider =
    match cfg.ProviderKind.ToLowerInvariant() with
    | "lmstudio" ->
        let url = cfg.LmStudioBaseUrl |> Option.defaultValue "http://localhost:1234"
        LmStudio url
    | _ ->
        Ollama cfg.OllamaBaseUrl

/// Walk cwd, chunk every file, embed with provider, append to NDJSON.
/// Returns (filesIndexed, embeddingFailures).
let runFullScan (cfg: IndexConfig) (provider: EmbeddingProvider) (cwd: string) : Async<int * int> =
    async {
        use http = new HttpClient(Timeout = TimeSpan.FromSeconds 30.0)
        let chunksFile = chunksPath cwd
        // Overwrite existing index for a full scan.
        try
            let dir : string | null = Path.GetDirectoryName chunksFile
            match dir with
            | null -> ()
            | d -> Directory.CreateDirectory d |> ignore
            if File.Exists chunksFile then File.Delete chunksFile
        with _ -> ()
        let files =
            try Directory.EnumerateFiles(cwd, "*", SearchOption.AllDirectories) |> Seq.toList
            with _ -> []
        let mutable filesIndexed = 0
        let mutable failures = 0
        let now = DateTimeOffset.UtcNow.ToString("o")
        for file in files do
            let rel =
                if file.StartsWith(cwd, StringComparison.OrdinalIgnoreCase) then
                    file.[cwd.Length..].TrimStart(Path.DirectorySeparatorChar, '/')
                else file
            if not (isExcluded cfg.ExcludeGlobs rel) then
                let chunks = chunkFile cfg.ChunkLines cfg.OverlapLines file |> Seq.toList
                let mutable fileOk = false
                for (i, (startLine, endLine, text)) in chunks |> List.indexed do
                    let! vecOpt = embedAsync provider cfg.EmbeddingModel http text |> Async.AwaitTask
                    match vecOpt with
                    | None ->
                        failures <- failures + 1
                    | Some vec ->
                        let chunk = {
                            FilePath  = rel
                            ChunkIdx  = i
                            StartLine = startLine
                            EndLine   = endLine
                            Text      = text
                            Vector    = vec
                            ModelId   = cfg.EmbeddingModel
                            IndexedAt = now
                        }
                        appendChunk chunksFile chunk
                        fileOk <- true
                if fileOk then filesIndexed <- filesIndexed + 1
        return (filesIndexed, failures)
    }

/// Same as runFullScan but skips files whose LastWriteTimeUtc is older than
/// the most-recent IndexedAt timestamp for any existing chunk of that file.
let runIncrementalScan (cfg: IndexConfig) (provider: EmbeddingProvider) (cwd: string) : Async<int * int> =
    async {
        use http = new HttpClient(Timeout = TimeSpan.FromSeconds 30.0)
        let chunksFile = chunksPath cwd
        // Build map: relPath → max IndexedAt
        let existingChunks = loadChunks chunksFile
        let indexedAtMap =
            existingChunks
            |> List.groupBy (fun c -> c.FilePath)
            |> List.map (fun (fp, cs) ->
                let maxTs =
                    cs
                    |> List.choose (fun c ->
                        try Some (DateTimeOffset.Parse c.IndexedAt) with _ -> None)
                    |> (fun xs -> if xs.IsEmpty then DateTimeOffset.MinValue else List.max xs)
                fp, maxTs)
            |> Map.ofList
        let files =
            try Directory.EnumerateFiles(cwd, "*", SearchOption.AllDirectories) |> Seq.toList
            with _ -> []
        let mutable filesIndexed = 0
        let mutable failures = 0
        let now = DateTimeOffset.UtcNow.ToString("o")
        for file in files do
            let rel =
                if file.StartsWith(cwd, StringComparison.OrdinalIgnoreCase) then
                    file.[cwd.Length..].TrimStart(Path.DirectorySeparatorChar, '/')
                else file
            if not (isExcluded cfg.ExcludeGlobs rel) then
                let skip =
                    match Map.tryFind rel indexedAtMap with
                    | None -> false
                    | Some indexedAt ->
                        try
                            let mtime = FileInfo(file).LastWriteTimeUtc
                            DateTimeOffset(mtime) <= indexedAt
                        with _ -> false
                if not skip then
                    let chunks = chunkFile cfg.ChunkLines cfg.OverlapLines file |> Seq.toList
                    let mutable fileOk = false
                    for (i, (startLine, endLine, text)) in chunks |> List.indexed do
                        let! vecOpt = embedAsync provider cfg.EmbeddingModel http text |> Async.AwaitTask
                        match vecOpt with
                        | None ->
                            failures <- failures + 1
                        | Some vec ->
                            let chunk = {
                                FilePath  = rel
                                ChunkIdx  = i
                                StartLine = startLine
                                EndLine   = endLine
                                Text      = text
                                Vector    = vec
                                ModelId   = cfg.EmbeddingModel
                                IndexedAt = now
                            }
                            appendChunk chunksFile chunk
                            fileOk <- true
                    if fileOk then filesIndexed <- filesIndexed + 1
        return (filesIndexed, failures)
    }
