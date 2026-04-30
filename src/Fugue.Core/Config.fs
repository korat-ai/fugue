module Fugue.Core.Config

open System
open System.Diagnostics.CodeAnalysis
open System.IO
open System.Text.Json
open Fugue.Core.JsonContext

type ProviderConfig =
    | Anthropic of apiKey: string * model: string
    | OpenAI of apiKey: string * model: string
    | Ollama of endpoint: Uri * model: string

type UserAlignment = Left | Right

type UiConfig =
    { UserAlignment  : UserAlignment
      Locale         : string
      PromptTemplate : string
      Bell           : bool
      Theme          : string }

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
    { UserAlignment  = Left
      Locale         = defaultLocale ()
      PromptTemplate = "♩ "
      Bell           = false
      Theme          = "" }

type AppConfig =
    { Provider: ProviderConfig
      SystemPrompt: string option
      ProfileContent: string option
      MaxIterations: int
      MaxTokens: int option
      Ui: UiConfig
      BaseUrl: string option }

let loadProfile (name: string) : string option =
    let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
    let path = Path.Combine(home, ".fugue", "profiles", name + ".md")
    if File.Exists path then
        try Some (File.ReadAllText path)
        with _ -> None
    else None

type ConfigError =
    | NoConfigFound of help: string
    | InvalidConfig of reason: string

let private modelDisplayNames : (string * string) list =
    [ "claude-opus-4-7",        "Claude Opus 4.7"
      "claude-opus-4-5",        "Claude Opus 4.5"
      "claude-sonnet-4-6",      "Claude Sonnet 4.6"
      "claude-haiku-4-5",       "Claude Haiku 4.5"
      "claude-haiku-4",         "Claude Haiku 4"
      "claude-3-7-sonnet",      "Claude 3.7 Sonnet"
      "claude-3-5-sonnet",      "Claude 3.5 Sonnet"
      "claude-3-opus",          "Claude 3 Opus"
      "gpt-5",                  "GPT-5"
      "gpt-4o",                 "GPT-4o"
      "gpt-4-turbo",            "GPT-4 Turbo"
      "gpt-4",                  "GPT-4"
      "gpt-3.5",                "GPT-3.5"
      "o3",                     "o3"
      "o1",                     "o1"
      "llama3.3",               "Llama 3.3"
      "llama3.2",               "Llama 3.2"
      "llama3.1",               "Llama 3.1"
      "qwen2.5-coder",          "Qwen 2.5 Coder"
      "qwen2.5",                "Qwen 2.5"
      "deepseek-coder",         "DeepSeek Coder"
      "deepseek",               "DeepSeek"
      "mistral",                "Mistral"
      "mixtral",                "Mixtral"
      "phi-4",                  "Phi-4"
      "phi-3",                  "Phi-3"
      "gemma3",                 "Gemma 3"
      "gemma2",                 "Gemma 2" ]

/// Map a raw API model ID to a friendly display name.
/// Matches from the start of the ID; unknown IDs return unchanged.
let modelDisplayName (modelId: string) : string =
    modelDisplayNames
    |> List.tryFind (fun (prefix, _) -> modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    |> Option.map snd
    |> Option.defaultValue modelId

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

[<RequiresUnreferencedCode("Uses STJ reflection; AppConfigDto is preserved via TrimmerRootDescriptor")>]
[<RequiresDynamicCode("Uses STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
let private fromFile (path: string) : Result<ProviderConfig * int * int option * UiConfig * string option, string> =
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
                let promptTemplate =
                    uiDto
                    |> Option.bind (fun u -> Option.ofObj u.promptTemplate)
                    |> Option.filter (fun s -> not (String.IsNullOrWhiteSpace s))
                    |> Option.defaultValue "♩ "
                let bell = uiDto |> Option.map (fun u -> u.bell) |> Option.defaultValue false
                let theme = uiDto |> Option.bind (fun u -> Option.ofObj u.theme) |> Option.defaultValue ""
                let ui = { UserAlignment = alignment; Locale = locale; PromptTemplate = promptTemplate; Bell = bell; Theme = theme }
                let baseUrl = dto.baseUrl |> Option.ofObj |> Option.filter (fun s -> not (String.IsNullOrEmpty s))
                let maxTokens = if dto.maxTokens > 0 then Some dto.maxTokens else None
                p, dto.maxIterations, maxTokens, ui, baseUrl)
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
[<RequiresUnreferencedCode("Uses STJ reflection via fromFile; preserved via TrimmerRootDescriptor")>]
[<RequiresDynamicCode("Uses STJ reflection via fromFile; System.Text.Json is TrimmerRootAssembly")>]
let load (_argv: string[]) : Result<AppConfig, ConfigError> =
    match fromExplicitEnv () with
    | Some provider ->
        Ok { Provider = provider; SystemPrompt = None; ProfileContent = None; MaxIterations = 30; MaxTokens = None; Ui = defaultUi (); BaseUrl = None }
    | None ->
        let path = configPath ()
        if File.Exists path then
            match fromFile path with
            | Ok (p, mi, maxTokens, ui, baseUrl) ->
                let mi = if mi <= 0 then 30 else mi
                Ok { Provider = p; SystemPrompt = None; ProfileContent = None; MaxIterations = mi; MaxTokens = maxTokens; Ui = ui; BaseUrl = baseUrl }
            | Error e -> Error(InvalidConfig e)
        else
            match fromImplicitEnv () with
            | Some provider ->
                Ok { Provider = provider; SystemPrompt = None; ProfileContent = None; MaxIterations = 30; MaxTokens = None; Ui = defaultUi (); BaseUrl = None }
            | None ->
                Error(NoConfigFound (helpText ()))

[<RequiresUnreferencedCode("Uses STJ reflection; AppConfigDto is preserved via TrimmerRootDescriptor")>]
[<RequiresDynamicCode("Uses STJ reflection; System.Text.Json is TrimmerRootAssembly")>]
let saveToFile (cfg: AppConfig) : unit =
    let path = configPath ()
    let dir = Path.GetDirectoryName(path) |> Option.ofObj |> Option.defaultValue "."
    Directory.CreateDirectory(dir) |> ignore
    let uiDto =
        { userAlignment  = (match cfg.Ui.UserAlignment with Left -> "left" | Right -> "right")
          locale         = cfg.Ui.Locale
          promptTemplate = if cfg.Ui.PromptTemplate = "♩ " then null else cfg.Ui.PromptTemplate
          bell           = cfg.Ui.Bell
          theme          = if cfg.Ui.Theme = "" then null else cfg.Ui.Theme }
    let baseUrlVal = cfg.BaseUrl |> Option.toObj
    let maxTokensVal = cfg.MaxTokens |> Option.defaultValue 0
    let dto =
        match cfg.Provider with
        | Anthropic(k, m) -> { provider = "anthropic"; model = m; apiKey = k; ollamaEndpoint = ""; baseUrl = baseUrlVal; maxIterations = cfg.MaxIterations; maxTokens = maxTokensVal; ui = uiDto }
        | OpenAI(k, m)    -> { provider = "openai";    model = m; apiKey = k; ollamaEndpoint = ""; baseUrl = baseUrlVal; maxIterations = cfg.MaxIterations; maxTokens = maxTokensVal; ui = uiDto }
        | Ollama(e, m)    -> { provider = "ollama";    model = m; apiKey = ""; ollamaEndpoint = string e; baseUrl = baseUrlVal; maxIterations = cfg.MaxIterations; maxTokens = maxTokensVal; ui = uiDto }
    let json = JsonSerializer.Serialize<AppConfigDto>(dto, appConfigOptions)
    File.WriteAllText(path, json)
