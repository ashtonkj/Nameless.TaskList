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
    abstract member FetchSince : folder: string * sinceUid: uint32 -> RawEmail list

/// Persists the per-account IMAP poll cursor.
type IEmailCursorStore =
    abstract member Load : unit -> EmailCursor
    abstract member Save : cursor: EmailCursor -> unit
