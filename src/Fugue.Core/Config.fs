module Fugue.Core.Config

open System
open System.IO
open System.Text.Json
open Fugue.Core.JsonContext

type ProviderConfig =
    | Anthropic of apiKey: string * model: string
    | OpenAI of apiKey: string * model: string
    | Ollama of endpoint: Uri * model: string

type UserAlignment = Left | Right

type UiConfig =
    { UserAlignment: UserAlignment
      Locale:        string }

let private defaultLocale () =
    let envLoc =
        match Environment.GetEnvironmentVariable "FUGUE_LOCALE" with
        | null | "" -> None
        | s -> Some s
    match envLoc with
    | Some "ru" -> "ru"
    | Some "en" -> "en"
    | _ ->
        match System.Globalization.CultureInfo.CurrentCulture.TwoLetterISOLanguageName with
        | "ru" -> "ru"
        | _ -> "en"

let defaultUi () : UiConfig =
    { UserAlignment = Left
      Locale = defaultLocale () }

type AppConfig =
    { Provider: ProviderConfig
      SystemPrompt: string option
      MaxIterations: int
      Ui: UiConfig }

type ConfigError =
    | NoConfigFound of help: string
    | InvalidConfig of reason: string

let private configPath () =
    let home =
        match Environment.GetEnvironmentVariable("HOME") with
        | null | "" ->
            match Environment.GetEnvironmentVariable("USERPROFILE") with
            | null | "" -> "."
            | p -> p
        | p -> p
    Path.Combine(home, ".fugue", "config.json")

let private readEnv (name: string) =
    match Environment.GetEnvironmentVariable(name) with
    | null | "" -> None
    | v -> Some v

/// Read provider from FUGUE_PROVIDER (explicit user intent — highest priority).
let private fromExplicitEnv () : ProviderConfig option =
    match readEnv "FUGUE_PROVIDER" with
    | Some "anthropic" ->
        readEnv "ANTHROPIC_API_KEY"
        |> Option.map (fun k ->
            let model = readEnv "FUGUE_MODEL" |> Option.defaultValue "claude-opus-4-7"
            Anthropic(k, model))
    | Some "openai" ->
        readEnv "OPENAI_API_KEY"
        |> Option.map (fun k ->
            let model = readEnv "FUGUE_MODEL" |> Option.defaultValue "gpt-5"
            OpenAI(k, model))
    | Some "ollama" ->
        let ep = readEnv "OLLAMA_ENDPOINT" |> Option.defaultValue "http://localhost:11434"
        let model = readEnv "FUGUE_MODEL" |> Option.defaultValue "llama3.1"
        Some(Ollama(Uri ep, model))
    | _ -> None

/// Implicit env fallback used only when no config file and no explicit provider.
/// Reason: a stray ANTHROPIC_API_KEY in the shell shouldn't override an explicit
/// ollama configuration in ~/.fugue/config.json.
let private fromImplicitEnv () : ProviderConfig option =
    match readEnv "ANTHROPIC_API_KEY", readEnv "OPENAI_API_KEY" with
    | Some k, _ -> Some(Anthropic(k, "claude-opus-4-7"))
    | _, Some k -> Some(OpenAI(k, "gpt-5"))
    | _ -> None

let private fromFile (path: string) : Result<ProviderConfig * int * UiConfig, string> =
    try
        let json = File.ReadAllText path
        let dtoOrNull =
            JsonSerializer.Deserialize<AppConfigDto>(json, appConfigOptions)
        match dtoOrNull |> Option.ofObj with
        | None -> Error "config file is empty or not a JSON object"
        | Some dto ->
            let provider =
                match dto.provider with
                | "anthropic" -> Ok(Anthropic(dto.apiKey, dto.model))
                | "openai" -> Ok(OpenAI(dto.apiKey, dto.model))
                | "ollama" ->
                    let ep =
                        if String.IsNullOrEmpty dto.ollamaEndpoint then "http://localhost:11434"
                        else dto.ollamaEndpoint
                    Ok(Ollama(Uri ep, dto.model))
                | other -> Error $"unknown provider: {other}"
            provider |> Result.map (fun p ->
                // dto.ui can be null when "ui" block is missing — STJ doesn't init nested CLIMutable records.
                let uiDto = dto.ui |> Option.ofObj
                let alignmentStr = uiDto |> Option.bind (fun u -> Option.ofObj u.userAlignment)
                let localeStr    = uiDto |> Option.bind (fun u -> Option.ofObj u.locale)
                let alignment =
                    match alignmentStr with
                    | Some "right" -> Right
                    | _            -> Left
                let locale =
                    match localeStr with
                    | Some "ru" -> "ru"
                    | Some "en" -> "en"
                    | _         -> defaultLocale ()
                let ui = { UserAlignment = alignment; Locale = locale }
                p, dto.maxIterations, ui)
    with ex -> Error ex.Message

let private helpText () =
    """No Fugue configuration found.

Set environment variables:
    FUGUE_PROVIDER=anthropic ANTHROPIC_API_KEY=sk-ant-... fugue
    FUGUE_PROVIDER=openai    OPENAI_API_KEY=sk-... fugue
    FUGUE_PROVIDER=ollama    OLLAMA_ENDPOINT=http://localhost:11434 fugue

…or create ~/.fugue/config.json (see docs).
"""

/// Resolution order:
///   1. FUGUE_PROVIDER env (explicit user intent for THIS run)
///   2. ~/.fugue/config.json     (persisted user choice)
///   3. ANTHROPIC_API_KEY / OPENAI_API_KEY env (implicit fallback)
let load (_argv: string[]) : Result<AppConfig, ConfigError> =
    match fromExplicitEnv () with
    | Some provider ->
        Ok { Provider = provider; SystemPrompt = None; MaxIterations = 30; Ui = defaultUi () }
    | None ->
        let path = configPath ()
        if File.Exists path then
            match fromFile path with
            | Ok (p, mi, ui) ->
                let mi = if mi <= 0 then 30 else mi
                Ok { Provider = p; SystemPrompt = None; MaxIterations = mi; Ui = ui }
            | Error e -> Error(InvalidConfig e)
        else
            match fromImplicitEnv () with
            | Some provider ->
                Ok { Provider = provider; SystemPrompt = None; MaxIterations = 30; Ui = defaultUi () }
            | None ->
                Error(NoConfigFound (helpText ()))

let saveToFile (cfg: AppConfig) : unit =
    let path = configPath ()
    let dir = Path.GetDirectoryName(path) |> Option.ofObj |> Option.defaultValue "."
    Directory.CreateDirectory(dir) |> ignore
    let uiDto =
        { userAlignment = (match cfg.Ui.UserAlignment with Left -> "left" | Right -> "right")
          locale        = cfg.Ui.Locale }
    let dto =
        match cfg.Provider with
        | Anthropic(k, m) -> { provider = "anthropic"; model = m; apiKey = k; ollamaEndpoint = ""; maxIterations = cfg.MaxIterations; ui = uiDto }
        | OpenAI(k, m)    -> { provider = "openai";    model = m; apiKey = k; ollamaEndpoint = ""; maxIterations = cfg.MaxIterations; ui = uiDto }
        | Ollama(e, m)    -> { provider = "ollama";    model = m; apiKey = ""; ollamaEndpoint = string e; maxIterations = cfg.MaxIterations; ui = uiDto }
    let json = JsonSerializer.Serialize<AppConfigDto>(dto, appConfigOptions)
    File.WriteAllText(path, json)
