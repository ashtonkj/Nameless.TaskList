namespace Nameless.TaskList.Core

open Nameless.TaskList.Core.KnowledgeBase
open Nameless.TaskList.Core.Prompts

/// Pure helpers for building and reconciling person-to-person relationship edges.
module Relationships =

    let private validRelations =
        set [ "parent-child"; "sibling"; "partner"; "patient-doctor"
              "student-teacher"; "colleague"; "friend"; "other" ]

    let confidenceRank (c: string) : int =
        match (if isNull c then "" else c).ToLowerInvariant().Trim() with
        | "high" -> 2
        | "medium" -> 1
        | "low" -> 0
        | _ -> -1

    /// Build a Relationship from an extracted edge. `resolve` maps a person slug to its
    /// file path (None = unknown person). Returns None when the edge is unusable.
    let buildEdge (resolve: string -> string option) (source: string) (edge: RelationshipEdge) : Relationship option =
        let fromSlug = Naming.slug edge.From
        let toSlug = Naming.slug edge.To
        let rel = (if isNull edge.Relation then "" else edge.Relation).ToLowerInvariant().Trim()
        if System.String.IsNullOrWhiteSpace fromSlug
           || System.String.IsNullOrWhiteSpace toSlug
           || fromSlug = toSlug then None
        elif not (Set.contains rel validRelations) then None
        else
            match resolve fromSlug, resolve toSlug with
            | Some fromPath, Some toPath ->
                let conf =
                    let c = (if isNull edge.Confidence then "" else edge.Confidence).ToLowerInvariant().Trim()
                    if confidenceRank c >= 0 then c else "low"
                Some { Type = "Relationship"
                       Title = sprintf "%s ↔ %s" fromSlug toSlug
                       From = fromPath
                       To = toPath
                       Relation = rel
                       Descriptor = (if isNull edge.Descriptor then "" else edge.Descriptor)
                       Confidence = conf
                       People = [| fromSlug; toSlug |]
                       Source = source }
            | _ -> None

    /// Decide whether to write `incoming` given any `existing` edge at the canonical path.
    let reconcile (existing: Relationship option) (incoming: Relationship) : Relationship option =
        match existing with
        | None -> Some incoming
        | Some ex ->
            if confidenceRank incoming.Confidence > confidenceRank ex.Confidence then Some incoming
            else None
