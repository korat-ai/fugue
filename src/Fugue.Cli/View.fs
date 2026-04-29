module Fugue.Cli.View

open Terminal.Gui.Input
open Fugue.Cli.Model
open Fugue.Cli.Mvu

let private renderBlockText (b: Block) : string =
    match b with
    | UserMsg t -> sprintf "user> %s" t
    | AssistantText sb -> sprintf "assistant> %s" (string sb)
    | ToolBlock(_, name, args, Running) ->
        sprintf "⟳ %s(%s)" name args
    | ToolBlock(_, name, args, Done out) ->
        let snippet = if out.Length > 200 then out.Substring(0, 200) + "…" else out
        sprintf "✓ %s(%s) → %s" name args snippet
    | ToolBlock(_, name, args, FailedTool err) ->
        sprintf "✗ %s(%s) → ERROR: %s" name args err
    | ErrorBlock e -> sprintf "⚠ %s" e

let view (model: Model) (dispatch: Msg -> unit) : ViewNode =
    let titleStr =
        match model.Config.Provider with
        | Fugue.Core.Config.Anthropic(_, m) -> sprintf "Fugue — %s" m
        | Fugue.Core.Config.OpenAI(_, m) -> sprintf "Fugue — %s" m
        | Fugue.Core.Config.Ollama(_, m) -> sprintf "Fugue — ollama:%s" m

    // Render history as a stack of Labels inside a Frame.
    let chatChildren =
        model.History
        |> List.mapi (fun i block ->
            Label(PAbs 1, PAbs i, renderBlockText block))

    let inputTitle =
        if model.Streaming then "input — generating, Esc to cancel" else "input"

    // Key.Q is a static property; .WithCtrl is an instance property on Key — gives Ctrl+Q.
    // Application.TopRunnable is a static property; Application.RequestStop(IRunnable) is static.
    Window(titleStr, [
        Frame("chat",
            x = PAbs 0, y = PAbs 0,
            w = DFill, h = DPercent 80,
            children = chatChildren)

        Frame(inputTitle,
            x = PAbs 0, y = PPercent 80,
            w = DFill, h = DPercent 18,
            children = [
                TextField(PAbs 1, PAbs 0, DFill, model.Input, fun s -> dispatch (InputChanged s))
            ])

        StatusBar [
            Key.Enter, "Send", (fun () -> dispatch Submit)
            Key.Esc, "Cancel", (fun () -> dispatch Cancel)
            // Ctrl+Q: Key.Q is a static prop, .WithCtrl is an instance prop returning a modified Key.
            // Call Mvu.requestStop() to stop the TG event loop via the stored IApplication ref.
            Key.Q.WithCtrl, "Quit", (fun () ->
                dispatch Quit
                Mvu.requestStop ())
        ]
    ])
