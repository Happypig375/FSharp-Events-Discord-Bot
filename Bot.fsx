#r "nuget: Ical.NET, 4.2.0"
#r "nuget: Discord.NET, 3.9.0"
// F#-related Discord servers that may schedule events
let sourceGuilds = Map [
    716980335593914419UL, "https://discord.gg/bpTJMbSSYK" // Fabulous
    940511234179096586UL, "https://discord.gg/D5QXvQrBVa" // Fantomas
]
task {
    printfn "Started."
    use http = new System.Net.Http.HttpClient()
    // https://sergeytihon.com/f-events/
    use! calendarStream = http.GetStreamAsync "https://calendar.google.com/calendar/ical/retcpic7o1iggr3cmqio8lcu8k%40group.calendar.google.com/public/basic.ics"
    let calendarEvents = Ical.Net.Calendar.Load(calendarStream).Events
    use client = new Discord.WebSocket.DiscordSocketClient()
    client.add_Log(fun msg -> task { printfn $"{msg}" })
    do! client.LoginAsync(Discord.TokenType.Bot, System.Environment.GetEnvironmentVariable "BOT_LOGIN_TOKEN")
    do! client.StartAsync()
    let completion = System.Threading.Tasks.TaskCompletionSource()
    client.add_Ready(fun () -> task {
        printfn "Ready. Processing started."
        let sourceEvents =  [
            for KeyValue (id, invite) in sourceGuilds do
                let sourceGuild = client.GetGuild id
                // Don't crash if we're not in one of the source guilds
                if sourceGuild <> null then
                    let iconStream = http.GetStreamAsync sourceGuild.IconUrl
                    // Discord API limitation: see beliow, location max length 100
                    $"{sourceGuild.Name[..99 - invite.Length - 1]} {invite}", iconStream, sourceGuild.Events
        ]
        let now = System.DateTimeOffset.UtcNow
        let maxEnd = now.AddYears(5).AddSeconds(-1.)
        for guild in client.Guilds do
            if Map.containsKey guild.Id sourceGuilds then () else // Ignore source guilds
            let existingDiscordEvents = System.Linq.Enumerable.ToDictionary (guild.Events |> Seq.filter (fun e -> e.Creator.Id = client.CurrentUser.Id), fun e -> e.Location, e.Name)
            let syncOneEvent location name startTime description endTime icon = task {
                if now < startTime && startTime <= maxEnd && startTime <= endTime then // Discord API limitation: Don't add new already started events (including now) or start time >= 5 years into future (precise to seconds) or start time > end time (can equal)
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
                            let! _ = guild.CreateEventAsync(name, startTime, Discord.GuildScheduledEventType.External,
                                Discord.GuildScheduledEventPrivacyLevel.Private, description, System.Nullable endTime, System.Nullable(), location)
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
                            do! existingDiscordEvent.ModifyAsync(fun props ->
                                props.Name <- name
                                props.StartTime <- startTime
                                props.Type <- Discord.GuildScheduledEventType.External
                                props.PrivacyLevel <- Discord.GuildScheduledEventPrivacyLevel.Private
                                props.Description <- description
                                props.EndTime <- endTime
                                props.ChannelId <- Discord.Optional.Create(System.Nullable())
                                props.Location <- location
                                props.CoverImage <- new Discord.Image(icon: System.IO.Stream) |> System.Nullable |> Discord.Optional
                            )
                    with exn -> printfn $"Error processing '{location}' event '{name}' for '{guild}'.\n{exn}"
            }
            for e in calendarEvents do
                do! syncOneEvent "F# Events Calendar https://sergeytihon.com/f-events/" e.Summary e.DtStart.AsDateTimeOffset e.Description e.DtEnd.AsDateTimeOffset calendarStream
            for location, icon, e in sourceEvents do
                use! icon = icon
                for e in e do
                    do! syncOneEvent location e.Name e.StartTime e.Description (if e.EndTime.HasValue then e.EndTime.GetValueOrDefault() else e.StartTime.AddHours 1.) icon
            for remainingDiscordEvent in existingDiscordEvents.Values do
                if remainingDiscordEvent.StartTime > now then // Don't remove already started events
                    do! remainingDiscordEvent.DeleteAsync()
                    printfn $"Removed event '{remainingDiscordEvent.Name}' for '{guild}'."
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