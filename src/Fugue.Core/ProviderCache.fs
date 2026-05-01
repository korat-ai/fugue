module Fugue.Core.ProviderCache

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

// ── Cache directory ───────────────────────────────────────────────────────────

let private cacheDir () =
    match Environment.GetEnvironmentVariable "FUGUE_CACHE_DIR" |> Option.ofObj with
    | Some d when d <> "" -> d
    | _ ->
        let home =
            match Environment.GetEnvironmentVariable "HOME" |> Option.ofObj with
            | Some h when h <> "" -> h
            | _ ->
                match Environment.GetEnvironmentVariable "USERPROFILE" |> Option.ofObj with
                | Some h -> h
                | None -> "."
        Path.Combine(home, ".fugue", "cache")

// ── Cache file path ───────────────────────────────────────────────────────────

let private cacheFilePath (provider: Hooks.ContextProvider) : string =
    let bytes  = Encoding.UTF8.GetBytes(provider.Command)
    let hash   = SHA256.HashData(bytes)
    let hex    = Convert.ToHexString(hash).[..15].ToLowerInvariant()
    Path.Combine(cacheDir (), $"provider-{provider.Name}-{hex}.json")

// ── getCached ─────────────────────────────────────────────────────────────────

/// Returns cached stdout if present and within TTL, otherwise None.
/// Swallows all IO / parse exceptions.
let getCached (provider: Hooks.ContextProvider) : Async<string option> =
    async {
        if provider.TtlSeconds <= 0 then return None
        else
            try
                let path = cacheFilePath provider
                if not (File.Exists path) then return None
                else
                    use doc = JsonDocument.Parse(File.ReadAllText path)
                    let root = doc.RootElement
                    let mutable fetchedAtEl = Unchecked.defaultof<JsonElement>
                    let mutable stdoutEl    = Unchecked.defaultof<JsonElement>
                    let mutable ttlEl       = Unchecked.defaultof<JsonElement>
                    if root.TryGetProperty("fetchedAt", &fetchedAtEl)
                       && root.TryGetProperty("stdout",    &stdoutEl)
                       && root.TryGetProperty("ttlSeconds", &ttlEl)
                       && fetchedAtEl.ValueKind = JsonValueKind.String
                       && stdoutEl.ValueKind    = JsonValueKind.String
                    then
                        let fetchedAtStr = fetchedAtEl.GetString() |> Option.ofObj |> Option.defaultValue ""
                        if fetchedAtStr = "" then return None
                        else
                        let fetchedAt = DateTimeOffset.Parse(fetchedAtStr)
                        let age       = DateTimeOffset.UtcNow - fetchedAt
                        let ttl       = TimeSpan.FromSeconds(float provider.TtlSeconds)
                        if age < ttl then
                            return stdoutEl.GetString() |> Option.ofObj
                        else
                            return None
                    else return None
            with _ ->
                return None
    }

// ── writeCache ────────────────────────────────────────────────────────────────

/// Persist stdout to the cache. Swallows all IO exceptions.
let writeCache (provider: Hooks.ContextProvider) (stdout: string) : Async<unit> =
    async {
        if provider.TtlSeconds <= 0 then ()
        else
            try
                let dir = cacheDir ()
                Directory.CreateDirectory(dir) |> ignore
                let path = cacheFilePath provider
                use stream = new MemoryStream()
                use writer = new Utf8JsonWriter(stream)
                writer.WriteStartObject()
                writer.WriteNumber("ttlSeconds", provider.TtlSeconds)
                writer.WriteString("fetchedAt", DateTimeOffset.UtcNow.ToString("o"))
                writer.WriteString("stdout", stdout)
                writer.WriteEndObject()
                writer.Flush()
                File.WriteAllBytes(path, stream.ToArray())
            with _ ->
                ()
    }

// ── fetchProvider ─────────────────────────────────────────────────────────────

let private toHookDef (p: Hooks.ContextProvider) : Hooks.HookDef =
    { Command = p.Command; Async = false; TimeoutMs = None; Matcher = None }

/// Fetch a single provider: check cache, run command on miss, write cache on success.
/// Returns None on failure (non-zero exit, timeout, or exception).
let private fetchProvider (hookTimeoutMs: int) (provider: Hooks.ContextProvider) : Async<string option> =
    async {
        match! getCached provider with
        | Some cached -> return Some cached
        | None ->
            try
                let def = toHookDef provider
                let exitCode, stdout = Hooks.runSyncHookWithStdout def "{}" hookTimeoutMs
                if exitCode = 0 then
                    do! writeCache provider stdout
                    return Some stdout
                else
                    return None
            with _ ->
                return None
    }

// ── assembleWithProviders ─────────────────────────────────────────────────────

/// Run all context providers in parallel, assemble their output into
/// "[Context: name]\n<stdout>" blocks joined by double newline.
/// Returns "" if all providers failed or no providers given.
let assembleWithProviders (providers: Hooks.ContextProvider list) (hookTimeoutMs: int) : Async<string> =
    async {
        if providers.IsEmpty then return ""
        else
            let! results = providers |> List.map (fetchProvider hookTimeoutMs) |> Async.Parallel
            let assembled =
                Array.zip (List.toArray providers) results
                |> Array.choose (fun (p, r) -> r |> Option.map (fun s -> $"[Context: {p.Name}]\n{s}"))
                |> String.concat "\n\n"
            return assembled
    }
