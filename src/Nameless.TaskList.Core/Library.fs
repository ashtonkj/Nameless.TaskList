namespace Nameless.TaskList.Core

open System

module Queries =
    let GetMessageByIdAndChatJid =
        """
SELECT
    m.id,
    m.chat_jid,
    ch.name AS chat_name,
    CASE
        WHEN ch.jid LIKE '%@g.us' THEN ch.name
        ELSE COALESCE(
                NULLIF(cn.full_name,    ''),
                NULLIF(cn.first_name,   ''),
                NULLIF(cn.push_name,    ''),
                NULLIF(cn.business_name,''),
                ch.name
             )
        END AS normalized_chat_name,
    (m.chat_jid LIKE '%@g.us') AS is_group,
    m.sender,
    CASE
        WHEN m.is_from_me THEN 'Me'
        ELSE COALESCE(
                NULLIF(c.full_name,    ''),
                NULLIF(c.first_name,   ''),
                NULLIF(c.push_name,    ''),
                NULLIF(c.business_name,''),
                m.sender
             )
        END AS sender_name,
    c.push_name     AS sender_push_name,
    c.full_name     AS sender_saved_name,
    c.business_name AS sender_business_name,
    m.is_from_me,
    m.content,
    m.media_type,
    m.filename,
    m.album_id,
    m.album_index,
    m.timestamp
FROM messages m
         LEFT JOIN chats ch             ON ch.jid = m.chat_jid
         LEFT JOIN whatsmeow_contacts c  ON split_part(c.their_jid, '@', 1)  = m.sender
         LEFT JOIN whatsmeow_contacts cn ON split_part(cn.their_jid, '@', 1) = split_part(m.chat_jid, '@', 1)
WHERE chat_jid not like 'status%' AND m.id = @Id AND m.chat_jid = @ChatJid;
        """
        
    let GetPreviousMessagesByChatIdAndJid =
        """
WITH RecentMessages AS (SELECT m.id,
                               m.chat_jid,
                               ch.name                    AS chat_name,
                               CASE
                                   WHEN ch.jid LIKE '%@g.us' THEN ch.name
                                   ELSE COALESCE(
                                           NULLIF(cn.full_name, ''),
                                           NULLIF(cn.first_name, ''),
                                           NULLIF(cn.push_name, ''),
                                           NULLIF(cn.business_name, ''),
                                           ch.name
                                        )
                                   END                    AS normalized_chat_name,
                               (m.chat_jid LIKE '%@g.us') AS is_group,
                               m.sender,
                               CASE
                                   WHEN m.is_from_me THEN 'Me'
                                   ELSE COALESCE(
                                           NULLIF(c.full_name, ''),
                                           NULLIF(c.first_name, ''),
                                           NULLIF(c.push_name, ''),
                                           NULLIF(c.business_name, ''),
                                           m.sender
                                        )
                                   END                    AS sender_name,
                               c.push_name                AS sender_push_name,
                               c.full_name                AS sender_saved_name,
                               c.business_name            AS sender_business_name,
                               m.is_from_me,
                               m.content,
                               m.media_type,
                               m.filename,
                               m.album_id,
                               m.album_index,
                               m.timestamp
                        FROM messages m
                                 LEFT JOIN chats ch ON ch.jid = m.chat_jid
                                 LEFT JOIN whatsmeow_contacts c ON split_part(c.their_jid, '@', 1) = m.sender
                                 LEFT JOIN whatsmeow_contacts cn
                                           ON split_part(cn.their_jid, '@', 1) = split_part(m.chat_jid, '@', 1)
                        ORDER BY m.timestamp desc)
SELECT * FROM RecentMessages m
WHERE m.id <> @Id AND m.chat_jid = @ChatJid AND m.timestamp < @Timestamp
ORDER BY timestamp desc
LIMIT 5
        """

    let GetMessagesSince =
        """
SELECT
    m.id,
    m.chat_jid,
    ch.name AS chat_name,
    CASE
        WHEN ch.jid LIKE '%@g.us' THEN ch.name
        ELSE COALESCE(
                NULLIF(cn.full_name,    ''),
                NULLIF(cn.first_name,   ''),
                NULLIF(cn.push_name,    ''),
                NULLIF(cn.business_name,''),
                ch.name
             )
        END AS normalized_chat_name,
    (m.chat_jid LIKE '%@g.us') AS is_group,
    m.sender,
    CASE
        WHEN m.is_from_me THEN 'Me'
        ELSE COALESCE(
                NULLIF(c.full_name,    ''),
                NULLIF(c.first_name,   ''),
                NULLIF(c.push_name,    ''),
                NULLIF(c.business_name,''),
                m.sender
             )
        END AS sender_name,
    c.push_name     AS sender_push_name,
    c.full_name     AS sender_saved_name,
    c.business_name AS sender_business_name,
    m.is_from_me,
    m.content,
    m.media_type,
    m.filename,
    m.album_id,
    m.album_index,
    m.timestamp
FROM messages m
         LEFT JOIN chats ch             ON ch.jid = m.chat_jid
         LEFT JOIN whatsmeow_contacts c  ON split_part(c.their_jid, '@', 1)  = m.sender
         LEFT JOIN whatsmeow_contacts cn ON split_part(cn.their_jid, '@', 1) = split_part(m.chat_jid, '@', 1)
WHERE m.chat_jid NOT LIKE 'status%'
  AND m.timestamp >= @Since
  AND (@ChatJid IS NULL OR m.chat_jid = @ChatJid)
ORDER BY m.timestamp ASC;
        """

    let GetMediaBytes =
        """
SELECT media
FROM messages
WHERE id = @Id AND chat_jid = @ChatJid AND octet_length(media) > 0;
        """
        
type ChatMessage =
    {
        Id: string
        ChatJid: string
        ChatName: string
        NormalizedChatName: string
        IsGroup: bool
        SenderId: string
        SenderName: string
        SenderPushName: string
        SenderSavedName: string
        SenderBusinessName: string
        IsFromMe: bool
        Content: string
        MediaType: string
        FileName: string
        AlbumId: string
        AlbumIndex: int option
        Timestamp: DateTime
    }