/// Minimal MVU (Model-View-Update) layer for Terminal.Gui v2.0.1.
///
/// Deviations from TG v1 / TG.Elmish skeleton:
///   - Application.Create() returns IApplication (instance-based); no static Application.Init()
///   - Application.Top is gone; use app.TopRunnableView (IApplication.TopRunnableView)
///   - Application.Invoke(Action) exists both as static (Application class) and instance (IApplication)
///     We use the static Application.Invoke here for simplicity.
///   - Pos.Percent(int) — takes a single int (not float32)
///   - Dim.Percent(int, DimPercentMode) — requires the DimPercentMode enum argument
///   - Pos.AnchorEnd() and Pos.AnchorEnd(int) — both exist
///   - IApplication.Run(IRunnable, errorHandler) — Window implements IRunnable directly
///   - StatusBar ctor takes IEnumerable<Shortcut>
///   - Shortcut ctor: (Key, string commandText, Action, string helpText) — 4-arg
///   - TextField exposes .TextChanged (EventHandler, not generic) and .Value (string)
///   - View.RemoveAll() returns IReadOnlyCollection<View>, not unit
module Fugue.Cli.Mvu

open System
open System.Collections.Generic
open Terminal.Gui
open Terminal.Gui.App
open Terminal.Gui.Views
open Terminal.Gui.ViewBase
open Terminal.Gui.Input

// ---------------------------------------------------------------------------
// Cmd
// ---------------------------------------------------------------------------

/// A command is a list of side-effect subscriptions that receive a dispatch function.
type Cmd<'msg> = (('msg -> unit) -> unit) list

module Cmd =
    let none<'msg> : Cmd<'msg> = []
    let ofMsg (m: 'msg) : Cmd<'msg> = [ fun dispatch -> dispatch m ]
    let ofSub (sub: ('msg -> unit) -> unit) : Cmd<'msg> = [ sub ]
    let batch (cmds: Cmd<'msg> list) : Cmd<'msg> = List.concat cmds

// ---------------------------------------------------------------------------
// ViewNode AST
// ---------------------------------------------------------------------------

/// Position in the TUI layout.
type Pos =
    | PAbs      of int          // Pos.Absolute n
    | PPercent  of int          // Pos.Percent n
    | PAnchorEnd of int         // Pos.AnchorEnd n  (0 = right/bottom edge)
    | PCenter                   // Pos.Center()

/// Dimension in the TUI layout.
type Dim =
    | DAbs     of int           // Dim.Absolute n
    | DFill                     // Dim.Fill()       (fill remaining space)
    | DPercent of int           // Dim.Percent(n, DimPercentMode.ContentSize)

/// Very light-weight virtual-DOM.  Add cases as needed.
type ViewNode =
    /// Top-level window — passed directly to app.Run.
    | Window of title: string * children: ViewNode list
    /// A bordered frame container.
    | Frame  of title: string * x: Pos * y: Pos * w: Dim * h: Dim * children: ViewNode list
    /// A single-line text label.
    | Label  of x: Pos * y: Pos * text: string
    /// An editable text field.  onChange receives the new string value after each keystroke.
    | TextField of x: Pos * y: Pos * w: Dim * value: string * onChange: (string -> unit)
    /// Read-only multi-line text view with word-wrap and built-in scroll/selection.
    /// Used for chat history; auto-scrolls to bottom on rebuild.
    | TextView of x: Pos * y: Pos * w: Dim * h: Dim * text: string * readOnly: bool * wordWrap: bool
    /// A status bar at the bottom of the screen.
    | StatusBar of items: (Key * string * (unit -> unit)) list

// ---------------------------------------------------------------------------
// Pos / Dim converters
// ---------------------------------------------------------------------------

let private toTGPos (p: Pos) : Terminal.Gui.ViewBase.Pos =
    match p with
    | PAbs n      -> Pos.Absolute n
    | PPercent pct -> Pos.Percent pct           // Pos.Percent(int)
    | PAnchorEnd n -> Pos.AnchorEnd n           // Pos.AnchorEnd(int offset)
    | PCenter      -> Pos.Center()

let private toTGDim (d: Dim) : Terminal.Gui.ViewBase.Dim =
    match d with
    | DAbs n     -> Dim.Absolute n
    | DFill      -> Dim.Fill()
    | DPercent p -> Dim.Percent(p, DimPercentMode.ContentSize)

// ---------------------------------------------------------------------------
// App-level stop hook (for Quit handling from View without passing IApplication around)
// ---------------------------------------------------------------------------

/// Stores the live IApplication instance while Mvu.run is executing.
/// View.fs reads this to call RequestStop() when the user triggers Quit.
let mutable internal currentApp : IApplication option = None

/// Request the running Terminal.Gui event loop to stop.
/// Safe to call from any thread; no-op if called outside of Mvu.run.
let requestStop () =
    match currentApp with
    | Some app -> app.RequestStop()
    | None -> ()

// ---------------------------------------------------------------------------
// build: ViewNode -> View
// ---------------------------------------------------------------------------

let rec private buildView (node: ViewNode) : View =
    match node with

    | Window (title, kids) ->
        let w = new Window()
        w.Title <- title
        for k in kids do w.Add(buildView k) |> ignore
        w :> View

    | Frame (title, x, y, w, h, kids) ->
        let f = new FrameView()
        f.Title  <- title
        f.X      <- toTGPos x
        f.Y      <- toTGPos y
        f.Width  <- toTGDim w
        f.Height <- toTGDim h
        for k in kids do f.Add(buildView k) |> ignore
        f :> View

    | Label (x, y, text) ->
        let l = new Label()
        l.X    <- toTGPos x
        l.Y    <- toTGPos y
        l.Text <- text
        l :> View

    | TextField (x, y, w, value, onChange) ->
        let tf = new Terminal.Gui.Views.TextField()
        tf.X     <- toTGPos x
        tf.Y     <- toTGPos y
        tf.Width <- toTGDim w
        tf.Text  <- value
        // After a rerender we recreate the field with the latest model.Input; place the
        // caret at end-of-text so the next keystroke continues where the user left off.
        tf.SelectedStart <- value.Length
        // TextField.TextChanged fires after every change; read .Text (string) for current value.
        tf.TextChanged.Add(fun _ -> onChange (tf.Text))
        tf :> View

    | TextView (x, y, w, h, text, ro, ww) ->
        let tv = new Terminal.Gui.Views.TextView()
        tv.X        <- toTGPos x
        tv.Y        <- toTGPos y
        tv.Width    <- toTGDim w
        tv.Height   <- toTGDim h
        tv.ReadOnly <- ro
        tv.WordWrap <- ww
        tv.Text     <- text
        // Auto-scroll to the last line so newest chat content stays in view.
        tv.MoveEnd() |> ignore
        tv :> View

    | StatusBar items ->
        let shortcuts =
            items
            |> List.map (fun (key, label, action) ->
                // Shortcut ctor: (Key key, string commandText, Action action, string helpText)
                new Shortcut(key, label, Action(fun () -> action ()), ""))
            |> List.toSeq
        let sb = new Terminal.Gui.Views.StatusBar(shortcuts)
        sb :> View

/// Recursively find the first TextField in a view tree.
/// We use this to (a) move focus to the input on first paint, and
/// (b) restore focus + cursor position after a rerender clears all sub-views.
let rec private findFirstTextField (v: View) : Terminal.Gui.Views.TextField option =
    match v with
    | :? Terminal.Gui.Views.TextField as f -> Some f
    | _ ->
        let mutable found : Terminal.Gui.Views.TextField option = None
        for child in v.SubViews do
            if found.IsNone then
                found <- findFirstTextField child
        found

// ---------------------------------------------------------------------------
// run — main MVU loop
// ---------------------------------------------------------------------------

/// Start the MVU loop.
///
/// <param name="init">Returns the initial model and any startup commands.</param>
/// <param name="update">Pure reducer: msg * model -> new model * cmds.</param>
/// <param name="view">Pure render: model * dispatch -> ViewNode (must be Window at root).</param>
let run<'model, 'msg>
    (init   : unit -> 'model * Cmd<'msg>)
    (update : 'msg -> 'model -> 'model * Cmd<'msg>)
    (view   : 'model -> ('msg -> unit) -> ViewNode)
    : unit =

    use app = Application.Create().Init()
    currentApp <- Some app

    let mutable model, initCmd = init ()
    // dispatchRef so closures captured before dispatch is defined can still call it.
    let dispatchRef : ref<'msg -> unit> = ref (fun _ -> ())

    /// Re-render: rebuild view tree and splice updated children into the live top-level view.
    /// After rebuild we re-focus the input field so the user can keep typing — buildView
    /// also positions the caret at end-of-text via SelectedStart.
    let render () =
        let node = view model dispatchRef.Value
        let freshView = buildView node
        // app.TopRunnableView is View|null in TG v2 (nullable reference type).
        // We cannot mutate the top-level Runnable itself, so we clear its children and
        // re-add the freshly built sub-views.
        let topView = app.TopRunnableView   // : View | null
        match topView with
        | null -> ()
        | tv ->
            tv.RemoveAll() |> ignore
            match freshView with
            | :? Window as win ->
                // Drain children out of the fresh Window and add them to the live top.
                // SubViews is IReadOnlyCollection<View> (non-nullable elements).
                for k in win.SubViews do
                    tv.Add k |> ignore
            | other ->
                tv.Add other |> ignore
            // Restore focus onto the new TextField (single input field in MVP).
            findFirstTextField tv
            |> Option.iter (fun f -> f.SetFocus() |> ignore)
            tv.SetNeedsDraw()

    let dispatch (msg: 'msg) =
        // Compute next model (may be called from any thread).
        let m', cmd = update msg model
        model <- m'
        // Re-render and side-effects must run on the UI thread.
        // Use app.Invoke (instance method on IApplication) — the static
        // Application.Invoke is deprecated in TG v2.0.1.
        app.Invoke(Action(fun () -> render ()))
        for sub in cmd do
            app.Invoke(Action(fun () -> sub dispatchRef.Value))

    dispatchRef.Value <- dispatch

    // First render: build the initial window and run the app with it.
    let initialNode = view model dispatch
    let initialView =
        match buildView initialNode with
        | :? Window as w ->
            // Move focus to the input on first paint so the user can type immediately.
            // View.Initialized fires once after the view is attached and laid out.
            w.Initialized.Add(fun _ ->
                findFirstTextField w
                |> Option.iter (fun f -> f.SetFocus() |> ignore))
            w :> IRunnable
        | other ->
            let w = new Window()
            w.Add other |> ignore
            w :> IRunnable

    // Run startup commands before the event loop begins (they may only send messages,
    // which will be marshalled onto the UI thread via Invoke once the loop is live).
    for sub in initCmd do sub dispatch

    // Blocks until RequestStop / Ctrl+Q.
    app.Run(initialView, fun ex ->
        // Unhandled exception handler — return false to propagate, true to swallow.
        false) |> ignore
    currentApp <- None
