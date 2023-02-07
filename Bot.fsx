#r "nuget: Ical.NET, 4.2.0"
#r "nuget: Discord.NET, 3.9.0"
// F#-related Discord servers that may schedule events
let sourceGuilds = Map [
    716980335593914419UL, ("https://discord.gg/bpTJMbSSYK", "https://raw.githubusercontent.com/fabulous-dev/Fabulous/main/logo/logo-title.png") // Fabulous
    940511234179096586UL, ("https://discord.gg/D5QXvQrBVa", "https://cdn.discordapp.com/icons/940511234179096586/c48720faa474402341a73385e911510b.png") // Fantomas
]
task {
    printfn "Started."
    use client = new Discord.WebSocket.DiscordSocketClient()
    client.add_Log(fun msg -> task { printfn $"{msg}" })
    do! client.LoginAsync(Discord.TokenType.Bot, System.Environment.GetEnvironmentVariable "BOT_LOGIN_TOKEN")
    do! client.StartAsync()
    let completion = System.Threading.Tasks.TaskCompletionSource()
    client.add_Ready(fun () -> task {
        printfn "Ready. Processing started."
        use http = new System.Net.Http.HttpClient()
        // https://sergeytihon.com/f-events/
        use! calendarStream = http.GetStreamAsync "https://calendar.google.com/calendar/ical/retcpic7o1iggr3cmqio8lcu8k%40group.calendar.google.com/public/basic.ics"
        let calendarEvents = Ical.Net.Calendar.Load(calendarStream).Events
        let sourceGuildEvents =  [
            for KeyValue (id, (invite, coverImageUrl)) in sourceGuilds do
                let sourceGuild = client.GetGuild id
                // Don't crash if we're not in one of the source guilds
                if sourceGuild <> null then
                    // Discord API limitation: see beliow, location max length 100
                    $"{sourceGuild.Name[..99 - invite.Length - 1]} {invite}", coverImageUrl, sourceGuild.Events
        ]
        let now = System.DateTimeOffset.UtcNow
        let maxEnd = now.AddYears(5).AddSeconds(-1.)
        // Discord API limitation: Don't add new already started events (including now) or start time >= 5 years into future (precise to seconds) or start time > end time (can equal)
        let filterEventByTime startTime endTime = now < startTime && startTime <= maxEnd && startTime <= endTime
        printfn $"Initialized events. There are {calendarEvents.Count} F# calendar event(s) ({calendarEvents |> Seq.filter (fun e -> filterEventByTime e.DtStart.AsDateTimeOffset e.DtEnd.AsDateTimeOffset) |> Seq.length} applicable) \
                 and {sourceGuildEvents |> Seq.collect (fun (_, _, events) -> events) |> Seq.length} source guild event(s)."
        for guild in client.Guilds do
            if Map.containsKey guild.Id sourceGuilds then () else // Ignore source guilds
            let existingDiscordEvents = System.Linq.Enumerable.ToDictionary (guild.Events |> Seq.filter (fun e -> e.Creator.Id = client.CurrentUser.Id), fun e -> e.Location, e.Name)
            let syncOneEvent location name startTime description endTime coverImageUrl = task {
                if filterEventByTime startTime endTime then
                    try
                        // Discord API limitation: string length limits
                        let name = (name: string)[..99]
                        let description = (description: string)[..999]
                        let location = (location: string)[..99]
                        // Discord API limitation: end time < 5 years into the future (precise to seconds)
                        let endTime = min endTime maxEnd
                        match (existingDiscordEvents.Remove: _ -> _ * _) (location, name) with
                        | false, _ ->
                            printfn $"Creating '{location}' event '{name}' for '{guild}'..."
                            let mutable coverImage = System.Nullable()
                            match coverImageUrl with
                            | Some coverImageUrl ->
                                use! coverImageStream = http.GetStreamAsync(coverImageUrl: string)
                                coverImage <- new Discord.Image(coverImageStream: System.IO.Stream) |> System.Nullable
                            | None -> ()
                            let! _ = guild.CreateEventAsync(name, startTime, Discord.GuildScheduledEventType.External,
                                Discord.GuildScheduledEventPrivacyLevel.Private, description, System.Nullable endTime, System.Nullable(), location, coverImage)
                            ()
                        | true, existingDiscordEvent ->
                            if existingDiscordEvent.Name = name
                                && existingDiscordEvent.StartTime = startTime
                                && existingDiscordEvent.Type = Discord.GuildScheduledEventType.External
                                && existingDiscordEvent.PrivacyLevel = Discord.GuildScheduledEventPrivacyLevel.Private
                                && existingDiscordEvent.Description = description
                                && existingDiscordEvent.EndTime.HasValue
                                && existingDiscordEvent.EndTime.GetValueOrDefault() = endTime
                                && existingDiscordEvent.Location = location
                            then () else // Minimise request count
                            printfn $"Modifing '{location}' event '{name}' for '{guild}'..."
                            let mutable coverImage = System.Nullable()
                            match coverImageUrl with
                            | Some coverImageUrl ->
                                use! coverImageStream = http.GetStreamAsync(coverImageUrl: string)
                                coverImage <- new Discord.Image(coverImageStream: System.IO.Stream) |> System.Nullable
                            | None -> ()
                            do! existingDiscordEvent.ModifyAsync(fun props ->
                                props.Name <- name
                                props.StartTime <- startTime
                                props.Type <- Discord.GuildScheduledEventType.External
                                props.PrivacyLevel <- Discord.GuildScheduledEventPrivacyLevel.Private
                                props.Description <- description
                                props.EndTime <- endTime
                                props.ChannelId <- Discord.Optional.Create(System.Nullable())
                                props.Location <- location
                                props.CoverImage <- coverImage |> Discord.Optional
                            )
                    with exn -> printfn $"Error processing '{location}' event '{name}' for '{guild}'.\n{exn}"
            }
            for e in calendarEvents do
                do! syncOneEvent "F# Events Calendar https://sergeytihon.com/f-events/" e.Summary e.DtStart.AsDateTimeOffset e.Description e.DtEnd.AsDateTimeOffset None
            for location, coverImageUrl, e in sourceGuildEvents do
                for e in e do
                    do! syncOneEvent location e.Name e.StartTime e.Description
                         (if e.EndTime.HasValue then e.EndTime.GetValueOrDefault() else e.StartTime.AddHours 1.)
                         (Some <| if isNull e.CoverImageId then coverImageUrl else e.GetCoverImageUrl())
            for remainingDiscordEvent in existingDiscordEvents.Values do
                if remainingDiscordEvent.StartTime > now then // Don't remove already started events
                    printfn $"Removing event '{remainingDiscordEvent.Name}' for '{guild}'..."
                    do! remainingDiscordEvent.DeleteAsync()
        printfn "Processing finished."
        completion.TrySetResult() |> ignore
    })
    use cancel = new System.Threading.CancellationTokenSource(System.TimeSpan.FromMinutes 5.)
    cancel.Token.Register((fun () ->
        if completion.TrySetCanceled() then printfn "Cancelled processing due to not being ready after timeout."
    ), false) |> ignore
    do! completion.Task

    do! client.StopAsync()
    do! client.LogoutAsync()

    printfn "Finished."
} |> fun t -> t.Wait()
