namespace Nameless.TaskList.Core.Ports

open System
open Nameless.TaskList.Core
open Nameless.TaskList.Core.Conversation

/// Fetches messages from the WhatsApp store.
type IMessageSource =
    abstract member GetMessage : id: string * chatJid: string -> ChatMessage option
    abstract member GetRecent : chatJid: string * before: DateTime * excludingId: string -> ChatMessage list

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
