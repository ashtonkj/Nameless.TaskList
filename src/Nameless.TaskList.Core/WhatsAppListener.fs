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
            let id = str "id" |> Option.defaultValue ""
            let jid = str "chat_jid" |> Option.defaultValue ""
            if String.IsNullOrWhiteSpace id || String.IsNullOrWhiteSpace jid then
                None
            else
                let ts =
                    match DateTimeOffset.TryParse(Option.defaultValue "" (str "timestamp")) with
                    | true, d -> d
                    | _ -> DateTimeOffset.MinValue
                Some { Id = id; ChatJid = jid; Timestamp = ts }
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

    // The KB cursor is wall-clock in the configured timezone; a payload carries an offset timestamp.
    let private toWallClock (offset: System.TimeSpan) (ts: System.DateTimeOffset) = ts.ToOffset(offset).DateTime

    /// One connection session: LISTEN first (so live notifications buffer), then catch up from the
    /// stored cursor, then process live payloads — advancing + persisting the cursor per message.
    /// A bad payload is reported via `log` and skipped. Returns on cancellation; lets a connection
    /// failure propagate so the host can reconnect.
    let runSession
        (listener: INotificationListener) (cursorStore: IListenCursorStore)
        (messages: IMessageSource) (processOne: string -> string -> unit)
        (utcOffset: System.TimeSpan) (channel: string) (log: string -> unit)
        (token: System.Threading.CancellationToken) : unit =
        listener.Subscribe channel
        cursorStore.Save(catchUp messages processOne (cursorStore.Load()))
        try
            while not token.IsCancellationRequested do
                for payload in listener.WaitNext token do
                    match parse payload with
                    | Some p ->
                        processOne p.Id p.ChatJid
                        cursorStore.Save { Since = toWallClock utcOffset p.Timestamp }
                    | None -> log (sprintf "skipped unparseable NOTIFY payload: %s" payload)
        with :? System.OperationCanceledException -> ()
