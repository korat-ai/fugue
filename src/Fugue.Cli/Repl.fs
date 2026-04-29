module Fugue.Cli.Repl

open System
open System.Collections.Generic
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Agents.AI
open Spectre.Console
open Fugue.Core.Config
open Fugue.Core.Localization
open Fugue.Agent
open Fugue.Cli

/// Holder for the currently active stream's CancellationTokenSource.
/// Console.CancelKeyPress handler uses this to cancel mid-stream Ctrl+C.
type CancelSource() =
    let mutable streamCts : CancellationTokenSource option = None
    let mutable quit = false
    member _.QuitRequested = quit
    member _.RequestQuit() = quit <- true
    member _.Token = CancellationToken.None
    member _.NewStreamCts() =
        let cts = new CancellationTokenSource()
        streamCts <- Some cts
        cts
    member _.OnCtrlC (args: ConsoleCancelEventArgs | null) =
        match args with
        | null -> quit <- true
        | a ->
            match streamCts with
            | Some cts ->
                try cts.Cancel() with _ -> ()
                a.Cancel <- true
            | None ->
                quit <- true
                a.Cancel <- true
    interface IDisposable with
        member _.Dispose() =
            streamCts |> Option.iter (fun c -> try c.Dispose() with _ -> ())

let private finishState (prev: Render.ToolState) (isErr: bool) (output: string) : Render.ToolState =
    match prev with
    | Render.Running(n, a) ->
        if isErr then Render.Failed(n, a, output)
        else Render.Completed(n, a, output)
    | other -> other

let private streamAndRender
        (agent: AIAgent) (session: AgentSession | null) (input: string)
        (cfg: AppConfig) (cancelSrc: CancelSource) : Task<unit> = task {
    use streamCts = cancelSrc.NewStreamCts()
    let strings = pick cfg.Ui.Locale
    let assistantBuf = StringBuilder()
    let tools = Dictionary<string, Render.ToolState>()
    let toolOrder = ResizeArray<string>()

    let layout () =
        let parts = ResizeArray<Spectre.Console.Rendering.IRenderable>()
        if assistantBuf.Length > 0 then
            parts.Add(Render.assistantLive (assistantBuf.ToString()))
        for id in toolOrder do
            parts.Add(Render.toolBullet strings tools.[id])
        Rows(parts) :> Spectre.Console.Rendering.IRenderable

    let live = AnsiConsole.Live(layout()).AutoClear(true)

    try
        do! live.StartAsync(fun ctx -> task {
            let stream = Conversation.run agent session input streamCts.Token
            let enumerator = stream.GetAsyncEnumerator(streamCts.Token)
            let mutable hasNext = true
            while hasNext do
                let! step = enumerator.MoveNextAsync().AsTask()
                if step then
                    match enumerator.Current with
                    | Conversation.TextChunk t ->
                        assistantBuf.Append t |> ignore
                    | Conversation.ToolStarted(id, n, a) ->
                        if not (tools.ContainsKey id) then toolOrder.Add id
                        tools.[id] <- Render.Running(n, a)
                    | Conversation.ToolCompleted(id, o, err) ->
                        let prev =
                            if tools.ContainsKey id then tools.[id]
                            else Render.Running("?", "?")
                        tools.[id] <- finishState prev err o
                    | Conversation.Finished -> ()
                    | Conversation.Failed _ -> ()
                    ctx.UpdateTarget(layout())
                    ctx.Refresh()
                else hasNext <- false
        })
        // Live cleared. Now render final state.
        if assistantBuf.Length > 0 then
            AnsiConsole.Write(Render.assistantFinal (assistantBuf.ToString()))
            AnsiConsole.WriteLine()
        for id in toolOrder do
            AnsiConsole.Write(Render.toolBullet strings tools.[id])
            AnsiConsole.WriteLine()
    with
    | :? OperationCanceledException ->
        AnsiConsole.Write(Render.cancelled strings)
        AnsiConsole.WriteLine()
    | ex ->
        AnsiConsole.Write(Render.errorLine strings ex.Message)
        AnsiConsole.WriteLine()
}

let run (agent: AIAgent) (cfg: AppConfig) (cwd: string) : Task<unit> = task {
    use cancelSrc = new CancelSource()
    Console.CancelKeyPress.Add(fun args -> cancelSrc.OnCtrlC args)
    let! session = agent.CreateSessionAsync(CancellationToken.None)
    StatusBar.start cwd cfg

    try
        while not cancelSrc.QuitRequested do
            let! lineOpt = ReadLine.readAsync (Render.prompt cwd) cancelSrc.Token
            match lineOpt with
            | None ->
                cancelSrc.RequestQuit()
            | Some "" -> ()
            | Some s when s = "/exit" || s = "/quit" ->
                cancelSrc.RequestQuit()
            | Some s when s = "/clear" ->
                AnsiConsole.Clear()
                StatusBar.refresh ()
            | Some userInput ->
                AnsiConsole.Write(Render.userMessage cfg.Ui userInput)
                AnsiConsole.WriteLine()
                do! streamAndRender agent session userInput cfg cancelSrc
                StatusBar.refresh ()
    finally
        StatusBar.stop ()
}
