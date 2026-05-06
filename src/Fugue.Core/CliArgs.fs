module Fugue.Core.CliArgs

// FS3261 (nullness) suppressed for this file only.  Justification: the DSL
// wraps System.CommandLine 2.0, whose `GetValue<T>` is annotated `T?` to
// accommodate the abstract case of an unset value.  In our DSL every option
// and argument has either `withDefault` / `argDefault` or is `required` —
// both guarantee non-null after a successful parse.  `Unchecked.nonNull`
// could express the assertion, but its `'T : not null` constraint excludes
// value-type options (int, bool) which we need to support.  This is a
// narrowly-scoped suppression on a single bridging surface, NOT a way to
// silence real bugs — every real call goes through the `withDefault` /
// `required` invariant maintained by the constructors above.
#nowarn "3261"

open System
open System.CommandLine
open System.Threading
open System.Threading.Tasks

// Thin F# DSL over System.CommandLine 2.0.  Goals:
//
//   * One parser shared by `fugue` (JIT REPL) and `fugue-aot` (AOT headless)
//   * Type-safe `ctx.Get` — handles carry their phantom `'T` so parse results
//     never need cast/box on the call site
//   * Children of a command (options/arguments/subcommands) compose through
//     a single `CmdChild` list — no `obj`/`Symbol` casts in user code
//
// The DSL is an F# wrapper over a fundamentally mutation-based C# library.
// Inside, we mutate the SC types when applying modifiers (`withDefault`,
// `required`, …); from the F# side everything looks like value-returning
// functions — by convention we always return the same handle so chains stay
// clean.  The mutation is local to construction; ParseResult itself is
// immutable.

// --------------------------------------------------------------------------
// Typed handles
// --------------------------------------------------------------------------

/// Typed handle for a command-line option.
[<Struct>]
type Opt<'T> = internal { Inner: Option<'T> }

/// Typed handle for a positional argument.
[<Struct>]
type Arg<'T> = internal { Inner: Argument<'T> }

/// A child of a command — option, argument, or subcommand.
/// Created via `optOf` / `argOf` / `subOf`.
type CmdChild = internal CmdChild of Symbol

// --------------------------------------------------------------------------
// Option / Argument constructors
// --------------------------------------------------------------------------

/// Create an option `--name`.  Description is required for `--help` output.
let opt<'T> (name: string) (description: string) : Opt<'T> =
    let o = Option<'T>(name)
    o.Description <- description
    { Inner = o }

/// Add an alias (e.g. `-p` for `--print`).
let alias (a: string) (o: Opt<'T>) : Opt<'T> =
    o.Inner.Aliases.Add a
    o

/// Default value used when the flag is omitted.
let withDefault (v: 'T) (o: Opt<'T>) : Opt<'T> =
    o.Inner.DefaultValueFactory <- (fun _ -> v)
    o

/// Mark the option required (parse error if missing).
let required (o: Opt<'T>) : Opt<'T> =
    o.Inner.Required <- true
    o

/// Make the option recursive — visible to all subcommands of the owning command.
/// (Replaces the pre-2.0 "global option" concept.)
let recursive (o: Opt<'T>) : Opt<'T> =
    o.Inner.Recursive <- true
    o

/// Create a positional argument.
let arg<'T> (name: string) (description: string) : Arg<'T> =
    let a = Argument<'T>(name)
    a.Description <- description
    { Inner = a }

/// Default for the argument (omitted argv → this value).
let argDefault (v: 'T) (a: Arg<'T>) : Arg<'T> =
    a.Inner.DefaultValueFactory <- (fun _ -> v)
    a

// --------------------------------------------------------------------------
// CliContext — read parsed values inside a handler
// --------------------------------------------------------------------------

/// Inside a command handler, read parsed values via `ctx.Get`.
/// Caller invariant: every option / argument used here must have been built
/// with `withDefault` / `argDefault` or marked `required` — both guarantee
/// the value is non-null at this point.
type CliContext internal (parseResult: ParseResult) =
    member _.Get (o: Opt<'T>) : 'T = parseResult.GetValue o.Inner
    member _.Get (a: Arg<'T>) : 'T = parseResult.GetValue a.Inner
    member _.ParseResult : ParseResult = parseResult

// --------------------------------------------------------------------------
// Command composition
// --------------------------------------------------------------------------

/// A composed command (root or subcommand).
type Cmd = internal { Inner: Command }

let optOf (o: Opt<'T>) : CmdChild = CmdChild (o.Inner :> Symbol)
let argOf (a: Arg<'T>) : CmdChild = CmdChild (a.Inner :> Symbol)
let subOf (c: Cmd)     : CmdChild = CmdChild (c.Inner :> Symbol)

let inline private addChildren (target: Command) (children: CmdChild list) : unit =
    for CmdChild s in children do
        match s with
        | :? Option   as o -> target.Options.Add     o
        | :? Argument as a -> target.Arguments.Add   a
        | :? Command  as c -> target.Subcommands.Add c
        | _ ->
            invalidArg "children"
                $"Unsupported symbol type for command child: {s.GetType().FullName}"

/// Build a (sub)command with a synchronous handler returning an exit code.
let command
    (name: string)
    (description: string)
    (children: CmdChild list)
    (handler: CliContext -> int)
    : Cmd
    =
    let c = Command(name, description)
    addChildren c children
    c.SetAction(Func<ParseResult, int>(fun pr -> handler (CliContext pr)))
    { Inner = c }

/// Build a (sub)command with an async handler returning an exit code.
/// Use this when the handler awaits I/O — keeps cancellation honest.
let commandAsync
    (name: string)
    (description: string)
    (children: CmdChild list)
    (handler: CliContext -> CancellationToken -> Task<int>)
    : Cmd
    =
    let c = Command(name, description)
    addChildren c children
    c.SetAction(Func<ParseResult, CancellationToken, Task<int>>(fun pr ct ->
        handler (CliContext pr) ct))
    { Inner = c }

/// Build the root command.  `description` becomes the program's help banner.
let root
    (description: string)
    (children: CmdChild list)
    (handler: CliContext -> int)
    : Cmd
    =
    let c = RootCommand(description)
    addChildren c children
    c.SetAction(Func<ParseResult, int>(fun pr -> handler (CliContext pr)))
    { Inner = c :> Command }

let rootAsync
    (description: string)
    (children: CmdChild list)
    (handler: CliContext -> CancellationToken -> Task<int>)
    : Cmd
    =
    let c = RootCommand(description)
    addChildren c children
    c.SetAction(Func<ParseResult, CancellationToken, Task<int>>(fun pr ct ->
        handler (CliContext pr) ct))
    { Inner = c :> Command }

// --------------------------------------------------------------------------
// Parse / Invoke
// --------------------------------------------------------------------------

/// Parse argv against `root` and invoke the matched handler.
/// Returns the handler's exit code, or 1 on parse errors (printed to stderr).
let run (argv: string[]) (root: Cmd) : int =
    let pr = root.Inner.Parse(argv)
    if pr.Errors.Count > 0 then
        for e in pr.Errors do
            eprintfn "%s" e.Message
        1
    else
        pr.Invoke()

/// Async sibling of `run` — required for commands built with `commandAsync` /
/// `rootAsync`.  Don't mix sync and async handlers in one command tree.
let runAsync (argv: string[]) (root: Cmd) : Task<int> = task {
    let pr = root.Inner.Parse(argv)
    if pr.Errors.Count > 0 then
        for e in pr.Errors do
            eprintfn "%s" e.Message
        return 1
    else
        return! pr.InvokeAsync()
}

/// Parse-only path — inspect errors yourself and decide what to do.
/// Useful for callers that want to fall back to a non-CLI flow on parse failure.
let parse (argv: string[]) (root: Cmd) : ParseResult =
    root.Inner.Parse(argv)
