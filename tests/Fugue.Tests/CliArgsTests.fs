module Fugue.Tests.CliArgsTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open FsUnit.Xunit
open Fugue.Core
open Fugue.Core.CliArgs

// Capture stderr around a block — used to verify parse-error messages flow
// to stderr, since `run` prints errors before returning exit code 1.
let private captureStderr (action: unit -> 'T) : 'T * string =
    let original = Console.Error
    use writer = new StringWriter()
    Console.SetError(writer)
    try
        let result = action ()
        Console.Out.Flush()
        result, writer.ToString()
    finally
        Console.SetError(original)

// --------------------------------------------------------------------------
// Option parsing
// --------------------------------------------------------------------------

[<Fact>]
let ``opt with default returns the default when flag is absent`` () =
    let countOpt = opt<int> "--count" "How many" |> withDefault 7
    let mutable observed = 0

    let cmd =
        root "test" [ optOf countOpt ] (fun ctx ->
            observed <- ctx.Get countOpt
            0)

    let exitCode = run [||] cmd

    exitCode |> should equal 0
    observed |> should equal 7

[<Fact>]
let ``opt with default is overridden by flag value`` () =
    let countOpt = opt<int> "--count" "How many" |> withDefault 1
    let mutable observed = 0

    let cmd =
        root "test" [ optOf countOpt ] (fun ctx ->
            observed <- ctx.Get countOpt
            0)

    let exitCode = run [| "--count"; "42" |] cmd

    exitCode |> should equal 0
    observed |> should equal 42

[<Fact>]
let ``opt alias works`` () =
    let printOpt = opt<bool> "--print" "Print mode" |> alias "-p" |> withDefault false
    let mutable observed = false

    let cmd =
        root "test" [ optOf printOpt ] (fun ctx ->
            observed <- ctx.Get printOpt
            0)

    let exitCode = run [| "-p" |] cmd

    exitCode |> should equal 0
    observed |> should equal true

[<Fact>]
let ``required opt missing returns exit code 1 and prints error to stderr`` () =
    let nameOpt = opt<string> "--name" "Your name" |> required

    let cmd =
        root "test" [ optOf nameOpt ] (fun ctx ->
            // Handler should not be invoked on parse failure.
            failwith "handler must not run on parse error")

    let exitCode, errOut = captureStderr (fun () -> run [||] cmd)

    exitCode |> should equal 1
    errOut |> should not' (be EmptyString)

// --------------------------------------------------------------------------
// Argument parsing
// --------------------------------------------------------------------------

[<Fact>]
let ``positional argument is captured`` () =
    let promptArg = arg<string> "prompt" "The prompt"
    let mutable observed = ""

    let cmd =
        root "test" [ argOf promptArg ] (fun ctx ->
            observed <- ctx.Get promptArg
            0)

    let exitCode = run [| "hello world" |] cmd

    exitCode |> should equal 0
    observed |> should equal "hello world"

[<Fact>]
let ``argument with default falls back when omitted`` () =
    let promptArg = arg<string> "prompt" "The prompt" |> argDefault "(empty)"
    let mutable observed = ""

    let cmd =
        root "test" [ argOf promptArg ] (fun ctx ->
            observed <- ctx.Get promptArg
            0)

    let exitCode = run [||] cmd

    exitCode |> should equal 0
    observed |> should equal "(empty)"

// --------------------------------------------------------------------------
// Subcommand dispatch
// --------------------------------------------------------------------------

[<Fact>]
let ``subcommand handler is invoked, root handler is not`` () =
    let mutable hits = 0, 0  // (root, sub)

    let printArg = arg<string> "prompt" "What to print"

    let printCmd =
        command "print" "Print mode" [ argOf printArg ] (fun ctx ->
            let r, s = hits
            hits <- (r, s + 1)
            0)

    let rootCmd =
        root "test" [ subOf printCmd ] (fun _ ->
            let r, s = hits
            hits <- (r + 1, s)
            0)

    let exitCode = run [| "print"; "hi" |] rootCmd

    exitCode |> should equal 0
    hits |> should equal (0, 1)

[<Fact>]
let ``recursive option is visible to subcommand`` () =
    let verboseOpt =
        opt<bool> "--verbose" "Verbose"
        |> alias "-v"
        |> withDefault false
        |> recursive

    let mutable observed = false

    let echoCmd =
        command "echo" "Echo back" [] (fun ctx ->
            observed <- ctx.Get verboseOpt
            0)

    let rootCmd =
        root "test" [ optOf verboseOpt; subOf echoCmd ] (fun _ -> 0)

    let exitCode = run [| "echo"; "-v" |] rootCmd

    exitCode |> should equal 0
    observed |> should equal true

// --------------------------------------------------------------------------
// Async handlers
// --------------------------------------------------------------------------

[<Fact>]
let ``async handler runs and returns its exit code`` () = task {
    let mutable observed = ""
    let promptArg = arg<string> "prompt" "Prompt"

    let asyncCmd =
        rootAsync "test" [ argOf promptArg ] (fun ctx _ -> task {
            do! Task.Yield()
            observed <- ctx.Get promptArg
            return 7
        })

    let! exitCode = runAsync [| "hello" |] asyncCmd

    exitCode |> should equal 7
    observed |> should equal "hello"
}

// --------------------------------------------------------------------------
// Multiple options on one command
// --------------------------------------------------------------------------

[<Fact>]
let ``two options on same command both parse independently`` () =
    let nameOpt   = opt<string> "--name" "Name" |> withDefault "anon"
    let countOpt  = opt<int>    "--count" "How many" |> withDefault 1
    let mutable observed : (string * int) = ("", 0)

    let cmd =
        root "test" [ optOf nameOpt; optOf countOpt ] (fun ctx ->
            observed <- (ctx.Get nameOpt, ctx.Get countOpt)
            0)

    let exitCode = run [| "--name"; "bob"; "--count"; "3" |] cmd

    exitCode |> should equal 0
    observed |> should equal ("bob", 3)

// --------------------------------------------------------------------------
// Parse-only path
// --------------------------------------------------------------------------

[<Fact>]
let ``parse returns ParseResult without invoking handler`` () =
    let mutable handlerCalled = false

    let cmd =
        root "test" [] (fun _ ->
            handlerCalled <- true
            0)

    let pr = parse [||] cmd

    pr.Errors.Count |> should equal 0
    handlerCalled |> should equal false  // parse() doesn't invoke
