module Fugue.Core.Domain

open System

type Role =
    | User
    | Assistant
    | System
    | Tool

type ContentBlock =
    | Text of string
    | ToolUse of id: string * name: string * argsJson: string
    | ToolResult of id: string * output: string * isError: bool

type Message =
    { Role: Role
      Blocks: ContentBlock list
      Timestamp: DateTimeOffset }

    static member text (role: Role) (text: string) =
        { Role = role
          Blocks = [ Text text ]
          Timestamp = DateTimeOffset.UtcNow }
