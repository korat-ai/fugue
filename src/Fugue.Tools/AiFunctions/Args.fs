module Fugue.Tools.AiFunctions.Args

open System
open System.Text.Json
open Microsoft.Extensions.AI

/// Try to coerce a value from AIFunctionArguments into a string.
/// Accepts JsonElement (string kind) or raw string. Returns None for any other shape.
let tryGetStr (args: AIFunctionArguments) (key: string) : string option =
    match args.TryGetValue key with
    | true, (:? JsonElement as el) when el.ValueKind = JsonValueKind.String ->
        Option.ofObj (el.GetString())
    | true, (:? string as s) -> Some s
    | _ -> None

let getStr (args: AIFunctionArguments) (key: string) : string =
    match tryGetStr args key with
    | Some s -> s
    | None -> raise (ArgumentException(sprintf "Missing or non-string argument '%s'" key))

let tryGetInt (args: AIFunctionArguments) (key: string) : int option =
    match args.TryGetValue key with
    | true, (:? JsonElement as el) when el.ValueKind = JsonValueKind.Number ->
        Some (el.GetInt32())
    | true, (:? int as i) -> Some i
    | true, (:? int64 as i) -> Some (int i)
    | _ -> None

let tryGetBool (args: AIFunctionArguments) (key: string) : bool option =
    match args.TryGetValue key with
    | true, (:? JsonElement as el) when el.ValueKind = JsonValueKind.True -> Some true
    | true, (:? JsonElement as el) when el.ValueKind = JsonValueKind.False -> Some false
    | true, (:? bool as b) -> Some b
    | _ -> None
