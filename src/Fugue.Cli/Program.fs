module Fugue.Cli.Program

open Terminal.Gui
open Terminal.Gui.App
open Terminal.Gui.Views
open Terminal.Gui.ViewBase

[<EntryPoint>]
let main _ =
    use app = Application.Create().Init()

    let win = new Window()
    win.Title <- "Fugue — hello"

    let label = new Label()
    label.X <- Pos.Absolute 1
    label.Y <- Pos.Absolute 1
    label.Text <- "Hello, fugue! Press Ctrl+Q to quit."
    win.Add label |> ignore

    app.Run win |> ignore
    0
