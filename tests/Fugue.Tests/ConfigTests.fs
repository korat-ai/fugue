[<Xunit.Collection("Sequential")>]
module Fugue.Tests.ConfigTests

open System
open System.IO
open Xunit
open FsUnit.Xunit
open Fugue.Core.Config

[<Fact>]
let ``load returns NoConfigFound when env empty and no file`` () =
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null)
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null)
    Environment.SetEnvironmentVariable("FUGUE_PROVIDER", null)
    let tmpHome = IO.Path.Combine(IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    let result = load [||]
    match result with
    | Error (NoConfigFound _) -> ()
    | other -> failwithf "expected NoConfigFound, got %A" other

[<Fact>]
let ``load picks Anthropic when ANTHROPIC_API_KEY set and no config file`` () =
    // Isolate HOME so the user's real ~/.fugue/config.json doesn't shadow the
    // implicit-env fallback (file > implicit-env per Config.load priority).
    let tmpHome = IO.Path.Combine(IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-test")
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null)
    Environment.SetEnvironmentVariable("FUGUE_PROVIDER", null)
    let result = load [||]
    match result with
    | Ok cfg ->
        match cfg.Provider with
        | Anthropic("sk-ant-test", model) -> model |> should equal "claude-opus-4-7"
        | other -> failwithf "expected Anthropic, got %A" other
    | Error e -> failwithf "expected Ok, got %A" e

[<Fact>]
let ``load picks alignment=Right when ui.userAlignment=right`` () =
    let tmpHome = IO.Path.Combine(IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let dir = IO.Path.Combine(tmpHome, ".fugue")
    IO.Directory.CreateDirectory dir |> ignore
    let path = IO.Path.Combine(dir, "config.json")
    IO.File.WriteAllText(path, """{
        "provider":"ollama","model":"llama","apiKey":"","ollamaEndpoint":"http://localhost:11434",
        "maxIterations":30,"ui":{"userAlignment":"right","locale":"ru"}
    }""")
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null)
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null)
    Environment.SetEnvironmentVariable("FUGUE_PROVIDER", null)
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    match load [||] with
    | Ok cfg ->
        cfg.Ui.UserAlignment |> should equal Right
        cfg.Ui.Locale |> should equal "ru"
    | Error e -> failwithf "expected Ok, got %A" e

[<Fact>]
let ``load defaults Ui to Left when ui block missing`` () =
    let tmpHome = IO.Path.Combine(IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let dir = IO.Path.Combine(tmpHome, ".fugue")
    IO.Directory.CreateDirectory dir |> ignore
    let path = IO.Path.Combine(dir, "config.json")
    IO.File.WriteAllText(path, """{
        "provider":"ollama","model":"llama","apiKey":"","ollamaEndpoint":"http://localhost:11434",
        "maxIterations":30
    }""")
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null)
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null)
    Environment.SetEnvironmentVariable("FUGUE_PROVIDER", null)
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    match load [||] with
    | Ok cfg -> cfg.Ui.UserAlignment |> should equal Left
    | Error e -> failwithf "expected Ok, got %A" e

[<Fact>]
let ``load defaults locale via defaultLocale when ui has only userAlignment`` () =
    let tmpHome = IO.Path.Combine(IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let dir = IO.Path.Combine(tmpHome, ".fugue")
    IO.Directory.CreateDirectory dir |> ignore
    let path = IO.Path.Combine(dir, "config.json")
    IO.File.WriteAllText(path, """{
        "provider":"ollama","model":"llama","apiKey":"","ollamaEndpoint":"http://localhost:11434",
        "maxIterations":30,"ui":{"userAlignment":"right"}
    }""")
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null)
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null)
    Environment.SetEnvironmentVariable("FUGUE_PROVIDER", null)
    Environment.SetEnvironmentVariable("FUGUE_LOCALE", null)
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    match load [||] with
    | Ok cfg ->
        cfg.Ui.UserAlignment |> should equal Right
        // locale comes from CultureInfo fallback in defaultLocale (); just assert it's non-empty.
        cfg.Ui.Locale |> should not' (equal "")
    | Error e -> failwithf "expected Ok, got %A" e

[<Fact>]
let ``load reads baseUrl from config when present`` () =
    let tmpHome = IO.Path.Combine(IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let dir = IO.Path.Combine(tmpHome, ".fugue")
    IO.Directory.CreateDirectory dir |> ignore
    let path = IO.Path.Combine(dir, "config.json")
    IO.File.WriteAllText(path, """{
        "provider":"openai","model":"local","apiKey":"x","ollamaEndpoint":"",
        "baseUrl":"http://localhost:1234/v1","maxIterations":30,"ui":{"userAlignment":"left","locale":"en"}
    }""")
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null)
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null)
    Environment.SetEnvironmentVariable("FUGUE_PROVIDER", null)
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    match load [||] with
    | Ok cfg -> cfg.BaseUrl |> should equal (Some "http://localhost:1234/v1")
    | Error e -> failwithf "expected Ok, got %A" e

[<Fact>]
let ``load defaults BaseUrl=None when baseUrl absent`` () =
    let tmpHome = IO.Path.Combine(IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let dir = IO.Path.Combine(tmpHome, ".fugue")
    IO.Directory.CreateDirectory dir |> ignore
    let path = IO.Path.Combine(dir, "config.json")
    IO.File.WriteAllText(path, """{
        "provider":"openai","model":"local","apiKey":"x","ollamaEndpoint":"","maxIterations":30
    }""")
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null)
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null)
    Environment.SetEnvironmentVariable("FUGUE_PROVIDER", null)
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    match load [||] with
    | Ok cfg -> cfg.BaseUrl |> should equal None
    | Error e -> failwithf "expected Ok, got %A" e

[<Fact>]
let ``loadProfile returns Some content when profile file exists`` () =
    let tmpHome = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let profilesDir = Path.Combine(tmpHome, ".fugue", "profiles")
    Directory.CreateDirectory profilesDir |> ignore
    File.WriteAllText(Path.Combine(profilesDir, "senior-reviewer.md"), "You are a senior code reviewer.")
    // Override HOME so Environment.SpecialFolder.UserProfile resolves to tmpHome.
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    let result = loadProfile "senior-reviewer"
    result |> should equal (Some "You are a senior code reviewer.")

[<Fact>]
let ``loadProfile returns None when profile file does not exist`` () =
    let tmpHome = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory tmpHome |> ignore
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    let result = loadProfile "nonexistent"
    result |> should equal (None: string option)

[<Fact>]
let ``load reads emojiMode=always from config`` () =
    let tmpHome = IO.Path.Combine(IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let dir = IO.Path.Combine(tmpHome, ".fugue")
    IO.Directory.CreateDirectory dir |> ignore
    let path = IO.Path.Combine(dir, "config.json")
    IO.File.WriteAllText(path, """{
        "provider":"ollama","model":"llama","apiKey":"","ollamaEndpoint":"http://localhost:11434",
        "maxIterations":30,"ui":{"userAlignment":"left","emojiMode":"always"}
    }""")
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null)
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null)
    Environment.SetEnvironmentVariable("FUGUE_PROVIDER", null)
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    match load [||] with
    | Ok cfg -> cfg.Ui.EmojiMode |> should equal "always"
    | Error e -> failwithf "expected Ok, got %A" e

[<Fact>]
let ``load reads emojiMode=never from config`` () =
    let tmpHome = IO.Path.Combine(IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let dir = IO.Path.Combine(tmpHome, ".fugue")
    IO.Directory.CreateDirectory dir |> ignore
    let path = IO.Path.Combine(dir, "config.json")
    IO.File.WriteAllText(path, """{
        "provider":"ollama","model":"llama","apiKey":"","ollamaEndpoint":"http://localhost:11434",
        "maxIterations":30,"ui":{"userAlignment":"left","emojiMode":"never"}
    }""")
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null)
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null)
    Environment.SetEnvironmentVariable("FUGUE_PROVIDER", null)
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    match load [||] with
    | Ok cfg -> cfg.Ui.EmojiMode |> should equal "never"
    | Error e -> failwithf "expected Ok, got %A" e

[<Fact>]
let ``load defaults emojiMode=auto when field absent`` () =
    let tmpHome = IO.Path.Combine(IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let dir = IO.Path.Combine(tmpHome, ".fugue")
    IO.Directory.CreateDirectory dir |> ignore
    let path = IO.Path.Combine(dir, "config.json")
    IO.File.WriteAllText(path, """{
        "provider":"ollama","model":"llama","apiKey":"","ollamaEndpoint":"http://localhost:11434",
        "maxIterations":30,"ui":{"userAlignment":"left"}
    }""")
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null)
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null)
    Environment.SetEnvironmentVariable("FUGUE_PROVIDER", null)
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    match load [||] with
    | Ok cfg -> cfg.Ui.EmojiMode |> should equal "auto"
    | Error e -> failwithf "expected Ok, got %A" e

[<Fact>]
let ``saveToFile round-trips emojiMode=never`` () =
    let tmpHome = IO.Path.Combine(IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    IO.Directory.CreateDirectory tmpHome |> ignore
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null)
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null)
    Environment.SetEnvironmentVariable("FUGUE_PROVIDER", null)
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    let cfg = { Provider = Anthropic("key", "model"); SystemPrompt = None; ProfileContent = None
                MaxIterations = 10; MaxTokens = None; BaseUrl = None
                Ui = { UserAlignment = Left; Locale = "en"; PromptTemplate = "♩ "; Bell = false; Theme = ""; EmojiMode = "never" } }
    saveToFile cfg
    match load [||] with
    | Ok loaded -> loaded.Ui.EmojiMode |> should equal "never"
    | Error e -> failwithf "expected Ok, got %A" e

[<Fact>]
let ``saveToFile omits emojiMode when auto (default)`` () =
    let tmpHome = IO.Path.Combine(IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    IO.Directory.CreateDirectory tmpHome |> ignore
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null)
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null)
    Environment.SetEnvironmentVariable("FUGUE_PROVIDER", null)
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    let cfg = { Provider = Anthropic("key", "model"); SystemPrompt = None; ProfileContent = None
                MaxIterations = 10; MaxTokens = None; BaseUrl = None
                Ui = { UserAlignment = Left; Locale = "en"; PromptTemplate = "♩ "; Bell = false; Theme = ""; EmojiMode = "auto" } }
    saveToFile cfg
    let json = IO.File.ReadAllText(IO.Path.Combine(tmpHome, ".fugue", "config.json"))
    json.Contains "emojiMode" |> should equal false

[<Fact>]
let ``load treats unknown emojiMode value as auto`` () =
    let tmpHome = IO.Path.Combine(IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    let dir = IO.Path.Combine(tmpHome, ".fugue")
    IO.Directory.CreateDirectory dir |> ignore
    let path = IO.Path.Combine(dir, "config.json")
    IO.File.WriteAllText(path, """{
        "provider":"ollama","model":"llama","apiKey":"","ollamaEndpoint":"http://localhost:11434",
        "maxIterations":30,"ui":{"userAlignment":"left","emojiMode":"bogus"}
    }""")
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null)
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null)
    Environment.SetEnvironmentVariable("FUGUE_PROVIDER", null)
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    match load [||] with
    | Ok cfg -> cfg.Ui.EmojiMode |> should equal "auto"
    | Error e -> failwithf "expected Ok, got %A" e

[<Fact>]
let ``AppConfig ProfileContent is None after load when no profile passed`` () =
    let tmpHome = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory tmpHome |> ignore
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-test")
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", null)
    Environment.SetEnvironmentVariable("FUGUE_PROVIDER", null)
    Environment.SetEnvironmentVariable("HOME", tmpHome)
    match load [||] with
    | Ok cfg -> cfg.ProfileContent |> should equal (None: string option)
    | Error e -> failwithf "expected Ok, got %A" e
