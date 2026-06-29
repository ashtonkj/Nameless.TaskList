namespace Nameless.TaskList.Core.Ports

open System
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Conversation

/// Fetches messages from the WhatsApp store.
type IMessageSource =
    abstract member GetMessage : id: string * chatJid: string -> ChatMessage option
    abstract member GetRecent : chatJid: string * before: DateTime * excludingId: string -> ChatMessage list
    abstract member GetMessagesSince : chatJid: string option * since: System.DateTime -> ChatMessage list
    abstract member GetMediaBytes : id: string * chatJid: string -> byte array option

/// Reads and writes markdown files relative to a vault root. Never deletes.
type IVault =
    abstract member Exists : relPath: string -> bool
    abstract member Read : relPath: string -> string
    abstract member Write : relPath: string * content: string -> unit
    abstract member ListFiles : relDir: string -> string list
    abstract member ListFilesRecursive : relDir: string -> string list
    /// Move a file from one vault-relative path to another, preserving its bytes. Never deletes
    /// data: src missing -> no-op; dst already exists -> no-op (never overwrite); otherwise the
    /// bytes move src -> dst and src is vacated. Returns unit and gives NO success signal: a caller
    /// that needs to know whether the move occurred (e.g. an active->active re-file where dst may
    /// already exist) must check `Exists dst` / `Exists src` itself first.
    abstract member Relocate : src: string * dst: string -> unit

/// One round-trip to the chat model. messages and tools are pre-serialized objects.
type IChatClient =
    abstract member Chat : messages: obj array * tools: obj array -> ChatResponse

/// Produces an embedding vector for a piece of text.
type IEmbedder =
    abstract member Embed : text: string -> float array

/// Describes an image (and transcribes any text in it) as plain text.
type IVision =
    abstract member Describe : imageBytes: byte array -> string

/// Transcribes spoken audio (e.g. a voice note) to plain text.
type ITranscriber =
    abstract member Transcribe : audioBytes: byte array -> string

/// Reads mail from an IMAP folder. Behind a port so the poller is testable offline.
type IMailbox =
    abstract member UidValidity : folder: string -> uint32
    /// The largest existing UID in the folder (0 when empty) — read cheaply (no bodies) so the
    /// poller can seed its cursor forward on first run without fetching the whole mailbox.
    abstract member HighestUid : folder: string -> uint32
    abstract member FetchSince : folder: string * sinceUid: uint32 -> RawEmail list

/// Persists the per-account IMAP poll cursor.
type IEmailCursorStore =
    abstract member Load : unit -> EmailCursor
    abstract member Save : cursor: EmailCursor -> unit

/// A persistent Postgres LISTEN connection, behind a port so the session loop is testable.
type INotificationListener =
    /// Issue LISTEN on the channel. After this, the server buffers notifications for delivery.
    abstract member Subscribe : channel: string -> unit
    /// Block until at least one notification is available, returning all currently-available
    /// payloads. Throws OperationCanceledException when the token is cancelled.
    abstract member WaitNext : token: System.Threading.CancellationToken -> string list

/// Persists the WhatsApp listener catch-up cursor.
type IListenCursorStore =
    abstract member Load : unit -> ListenCursor
    abstract member Save : cursor: ListenCursor -> unit

/// Persists the in-app scheduler's per-task last-run state.
type ISchedulerStateStore =
    abstract member Load : unit -> SchedulerState
    abstract member Save : state: SchedulerState -> unit

/// Persists the embedding cache (content-keyed vectors) across restarts.
type IEmbeddingCacheStore =
    abstract member Load : unit -> EmbeddingCacheState
    abstract member Save : state: EmbeddingCacheState -> unit
