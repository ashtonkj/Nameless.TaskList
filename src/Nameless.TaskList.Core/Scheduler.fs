namespace Nameless.TaskList.Core

open System

module Scheduler =

    /// A parsed schedule for one task.
    type ScheduleSpec =
        | Daily of hour:int * minute:int
        | Weekly of day:DayOfWeek * hour:int * minute:int

    /// One schedulable operation. Name is the state key + log label.
    type ScheduledTask = { Name: string; Spec: ScheduleSpec }

    let private parseHm (hm: string) : (int * int) option =
        match hm.Split(':') with
        | [| h; m |] ->
            match Int32.TryParse h, Int32.TryParse m with
            | (true, hh), (true, mm) when hh >= 0 && hh <= 23 && mm >= 0 && mm <= 59 -> Some(hh, mm)
            | _ -> None
        | _ -> None

    let private parseDay (d: string) : DayOfWeek option =
        match d.Trim().ToLowerInvariant() with
        | "mon" -> Some DayOfWeek.Monday
        | "tue" -> Some DayOfWeek.Tuesday
        | "wed" -> Some DayOfWeek.Wednesday
        | "thu" -> Some DayOfWeek.Thursday
        | "fri" -> Some DayOfWeek.Friday
        | "sat" -> Some DayOfWeek.Saturday
        | "sun" -> Some DayOfWeek.Sunday
        | _ -> None

    /// Parse a config time string. "07:00" -> Daily; "Mon 07:00" -> Weekly; anything else -> None.
    let parseSpec (s: string) : ScheduleSpec option =
        if String.IsNullOrWhiteSpace s then None
        else
            match s.Trim().Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries) with
            | [| hm |] -> parseHm hm |> Option.map (fun (h, m) -> Daily(h, m))
            | [| d; hm |] ->
                match parseDay d, parseHm hm with
                | Some dw, Some(h, m) -> Some(Weekly(dw, h, m))
                | _ -> None
            | _ -> None

    /// The latest scheduled datetime <= now.
    let mostRecentOccurrence (now: DateTime) (spec: ScheduleSpec) : DateTime =
        match spec with
        | Daily(h, m) ->
            let today = DateTime(now.Year, now.Month, now.Day, h, m, 0)
            if today <= now then today else today.AddDays(-1.0)
        | Weekly(d, h, m) ->
            // Walk back day by day from today at h:m to the most recent matching weekday <= now.
            let rec back (candidate: DateTime) =
                if candidate.DayOfWeek = d && candidate <= now then candidate
                else back (candidate.AddDays(-1.0))
            back (DateTime(now.Year, now.Month, now.Day, h, m, 0))

    /// Tasks whose most-recent scheduled slot is newer than their recorded last run.
    /// A never-run task fires only once the CURRENT day's slot has passed (recent.Date = now.Date),
    /// so first start does not retroactively run a slot that predates the app. A task that has run
    /// before uses plain catch-up (lastRun < recent), which fires a missed daily OR weekly slot once.
    let dueTasks (now: DateTime) (tasks: ScheduledTask list) (state: SchedulerState) : ScheduledTask list =
        tasks |> List.filter (fun t ->
            let recent = mostRecentOccurrence now t.Spec
            match state.LastRuns.TryGetValue t.Name with
            | true, lastRun -> lastRun < recent
            | _ -> recent.Date = now.Date)

    /// Run each due task via `run`, returning a NEW state with those tasks' last-run set to `now`.
    /// Does not mutate the input. The host service wraps each task so one failure never aborts the
    /// others; last-run advances regardless, so a failed run simply waits for its next slot rather
    /// than retrying every tick.
    let tick (now: DateTime) (tasks: ScheduledTask list) (state: SchedulerState) (run: ScheduledTask -> unit) : SchedulerState =
        let due = dueTasks now tasks state
        for t in due do run t
        let updated = System.Collections.Generic.Dictionary(state.LastRuns)
        for t in due do updated.[t.Name] <- now
        { LastRuns = updated }
