module Fugue.Cli.Model

open System
open System.Text
open System.Threading
open Fugue.Core.Config

type ToolStatus =
    | Running
    | Done of output: string
    | FailedTool of error: string

type Block =
    | UserMsg of string
    | AssistantText of StringBuilder
    | ToolBlock of id: string * name: string * args: string * status: ToolStatus
    | ErrorBlock of string

/// Note: Microsoft Agent Framework 1.3 renamed `AgentThread` → `AgentSession`.
type Model =
    { Config: AppConfig
      Session: Microsoft.Agents.AI.AgentSession option
      History: Block list
      Input: string
      Streaming: bool
      Cancel: CancellationTokenSource option
      Status: string }

type Msg =
    | InputChanged of string
    | Submit
    | StreamStarted of CancellationTokenSource
    | TextChunk of string
    | ToolStarted of id: string * name: string * args: string
    | ToolCompleted of id: string * output: string * isError: bool
    | StreamFinished
    | StreamFailed of string
    | Cancel
    | Quit
