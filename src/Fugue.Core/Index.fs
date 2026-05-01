module Fugue.Core.Index

open System
open System.Diagnostics.CodeAnalysis
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Serialization

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

[<CLIMutable; DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)>]
type EmbeddedChunk = {
    FilePath  : string
    ChunkIdx  : int
    StartLine : int
    EndLine   : int
    Text      : string
    Vector    : float32[]
    ModelId   : string
    IndexedAt : string
}

type IndexConfig = {
    EmbeddingModel   : string
    OllamaBaseUrl    : string
    LmStudioBaseUrl  : string option
    ProviderKind     : string   // "ollama" | "lmstudio"
    ChunkLines       : int
    OverlapLines     : int
    ExcludeGlobs     : string list
}

let defaultIndexConfig : IndexConfig = {
    EmbeddingModel  = "nomic-embed-text"
    OllamaBaseUrl   = "http://localhost:11434"
    LmStudioBaseUrl = None
    ProviderKind    = "ollama"
    ChunkLines      = 40
    OverlapLines    = 10
    ExcludeGlobs    = [ "**/.git/**"; "**/node_modules/**"; "**/bin/**"; "**/obj/**"
                        "**/*.min.js"; "**/*.min.css"; "**/*.map" ]
}

// ---------------------------------------------------------------------------
// JSON options (camelCase, ignore null — same as appConfigOptions)
// ---------------------------------------------------------------------------

let private chunkJsonOptions =
    let opts = JsonSerializerOptions()
    opts.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase
    opts.DefaultIgnoreCondition <- JsonIgnoreCondition.WhenWritingNull
    opts

// ---------------------------------------------------------------------------
// Directory & path helpers
// ---------------------------------------------------------------------------

/// Root directory for all vector index files.
/// Honors FUGUE_INDEX_DIR env override (used by tests for full isolation).
let indexDir () : string =
    match Environment.GetEnvironmentVariable "FUGUE_INDEX_DIR" |> Option.ofObj with
    | Some d when d <> "" -> d
    | _ ->
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fugue", "index")

/// SHA256 of the canonical cwd string; first 16 hex chars.
/// Uses the static HashData overload — AOT-clean, no reflection.
let workspaceHash (cwd: string) : string =
    let bytes = Encoding.UTF8.GetBytes(cwd.TrimEnd('/', '\\'))
    let hash = SHA256.HashData(bytes)
    BitConverter.ToString(hash, 0, 8).Replace("-", "").ToLowerInvariant()

/// Path to the chunks NDJSON file for a given workspace.
let chunksPath (cwd: string) : string =
    Path.Combine(indexDir (), workspaceHash cwd, "chunks.ndjson")

// ---------------------------------------------------------------------------
// NDJSON persistence
// ---------------------------------------------------------------------------

/// Append a single EmbeddedChunk as one NDJSON line.
/// Best-effort: IO errors are silently swallowed (same policy as SessionPersistence.appendRecord).
let appendChunk (path: string) (chunk: EmbeddedChunk) : unit =
    try
        let dir : string | null = Path.GetDirectoryName path
        match dir with
        | null -> ()
        | d -> Directory.CreateDirectory d |> ignore
        let line = JsonSerializer.Serialize(chunk, chunkJsonOptions)
        File.AppendAllText(path, line + "\n")
    with _ -> ()

/// Read all EmbeddedChunk lines from an NDJSON file.
/// Malformed lines are silently skipped; returns [] on any error.
let loadChunks (path: string) : EmbeddedChunk list =
    try
        if not (File.Exists path) then []
        else
            File.ReadAllLines path
            |> Array.toList
            |> List.choose (fun line ->
                let l = line.Trim()
                if l = "" then None
                else
                    try
                        let c : EmbeddedChunk | null = JsonSerializer.Deserialize<EmbeddedChunk>(l, chunkJsonOptions)
                        match c with
                        | null -> None
                        | v    -> Some v
                    with _ -> None)
    with _ -> []

// ---------------------------------------------------------------------------
// Vector math
// ---------------------------------------------------------------------------

/// Cosine similarity between two float32 vectors.
/// Returns 0.0f if either vector has zero norm.
let cosineSimilarity (a: float32[]) (b: float32[]) : float32 =
    if a.Length = 0 || b.Length = 0 || a.Length <> b.Length then 0.0f
    else
        let mutable dot  = 0.0f
        let mutable normA = 0.0f
        let mutable normB = 0.0f
        for i in 0 .. a.Length - 1 do
            dot   <- dot   + a.[i] * b.[i]
            normA <- normA + a.[i] * a.[i]
            normB <- normB + b.[i] * b.[i]
        let denom = MathF.Sqrt(normA) * MathF.Sqrt(normB)
        if denom = 0.0f then 0.0f else dot / denom

// ---------------------------------------------------------------------------
// Exclusion glob matching
// ---------------------------------------------------------------------------

/// Simple glob match: supports `*` (any chars except separator), `**` (any path segment),
/// and `?` (any single char except separator). Uses forward-slash normalisation.
/// Covers the patterns used in defaultIndexConfig exclusions — AOT-safe, no reflection.
let private globMatch (pattern: string) (path: string) : bool =
    let sep = '/'
    let norm (s: string) = s.Replace('\\', sep)
    let p = norm pattern
    let s = norm path
    // Split into segments for ** handling
    let pSegs = p.Split sep
    let sSegs = s.Split sep
    let rec matchSegs pi si =
        if pi = pSegs.Length && si = sSegs.Length then true
        elif pi = pSegs.Length then false
        elif pSegs.[pi] = "**" then
            // ** matches zero or more path segments
            let mutable found = false
            let mutable k = si
            while not found && k <= sSegs.Length do
                if matchSegs (pi + 1) k then found <- true
                k <- k + 1
            found
        elif si = sSegs.Length then false
        else
            // Match a single segment using * and ? wildcards
            let rec matchSeg pp sp =
                if pp = pSegs.[pi].Length && sp = sSegs.[si].Length then true
                elif pp = pSegs.[pi].Length then false
                elif sp = sSegs.[si].Length then
                    // consume trailing * in pattern
                    let mutable allStar = true
                    for k in pp .. pSegs.[pi].Length - 1 do
                        if pSegs.[pi].[k] <> '*' then allStar <- false
                    allStar
                else
                    let pc = pSegs.[pi].[pp]
                    let sc = sSegs.[si].[sp]
                    if pc = '*' then
                        // * matches zero or more non-sep chars
                        matchSeg (pp + 1) sp || matchSeg pp (sp + 1)
                    elif pc = '?' then
                        matchSeg (pp + 1) (sp + 1)
                    elif Char.ToLowerInvariant pc = Char.ToLowerInvariant sc then
                        matchSeg (pp + 1) (sp + 1)
                    else false
            if matchSeg 0 0 then matchSegs (pi + 1) (si + 1)
            else false
    matchSegs 0 0

/// Check whether a relative path matches any of the given glob patterns.
/// Handles `**`, `*`, and `?` wildcards. AOT-safe (no reflection).
let isExcluded (excludeGlobs: string list) (relPath: string) : bool =
    excludeGlobs |> List.exists (fun g -> globMatch g relPath)
