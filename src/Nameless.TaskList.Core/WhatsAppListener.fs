namespace Nameless.TaskList.Core

open System
open System.Text.Json
open Nameless.TaskList.Core.Ports

module WhatsAppListener =

    /// Decode a `whatsapp_new_message` NOTIFY payload. Tolerant: returns None on invalid JSON
    /// or when id/chat_jid are missing or blank. Never throws.
    let parse (json: string) : NotifyPayload option =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement
            let str (name: string) : string option =
                let mutable v = JsonElement()
                match root.TryGetProperty(name, &v) with
                | true when v.ValueKind = JsonValueKind.String -> Some (v.GetString())
                | _ -> None
            let id = str "id"
            let jid = str "chat_jid"
            if Option.isNone id || Option.isNone jid ||
               String.IsNullOrWhiteSpace (Option.defaultValue "" id) ||
               String.IsNullOrWhiteSpace (Option.defaultValue "" jid) then
                None
            else
                let ts =
                    match DateTimeOffset.TryParse(Option.defaultValue "" (str "timestamp")) with
                    | true, d -> d
                    | _ -> DateTimeOffset.MinValue
                Some { Id = Option.defaultValue "" id; ChatJid = Option.defaultValue "" jid; Timestamp = ts }
        with _ -> None

    /// Drain every message since the cursor (all chats, ascending — GetMessagesSince already
    /// orders by timestamp ASC) through processOne, returning the cursor advanced to the latest
    /// processed message's timestamp. Re-processing is safe (pipeline idempotency).
    let catchUp (messages: IMessageSource) (processOne: string -> string -> unit) (cursor: ListenCursor) : ListenCursor =
        let msgs = messages.GetMessagesSince(None, cursor.Since)
        let mutable latest = cursor.Since
        for m in msgs do
            processOne m.Id m.ChatJid
            if m.Timestamp > latest then latest <- m.Timestamp
        { Since = latest }
