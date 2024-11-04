using System.Diagnostics;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using DSharpPlus.VoiceNext;
using Serilog;

namespace MusicDemo; 

public class SlashCommands : ApplicationCommandModule {
    [SlashCommand("play", "Starts playing from the specified URL")]
    public async Task PlayCommand(InteractionContext ctx, 
        [Option("url", "URL of the video/music/radio you want to play")] string url) {
        try {
            var verify = await Verify(ctx);
            if (!verify) return;
            
            var error = new DiscordEmbedBuilder()
                .WithColor(DiscordColor.Red)
                .WithTitle("Goober's Music | Error occured");
            if (QueueSystem.IsDuplicate(ctx.Guild.Id, url)) {
                error.WithDescription("This song is a duplicate!");
                await ctx.CreateResponseAsync(new DiscordInteractionResponseBuilder()
                    .WithContent("").AddEmbed(error.Build()));
                return;
            }
            
            await ctx.CreateResponseAsync(new DiscordInteractionResponseBuilder()
                .WithContent("").AddEmbed(new DiscordEmbedBuilder()
                    .WithFooter("Give me a second, I'm processing the URL...")
                    .WithAuthor("In progress", iconUrl: "https://media.tenor.com/damu8hbJ19YAAAAC/shrug-emoji.gif")
                    .WithColor(DiscordColor.Yellow)
                    .WithTitle("Goober's Music | Play by URL")));
            var enqueue = QueueSystem.ShouldEnqueue(ctx.Guild.Id);
            AudioFetcher.Result? direct;
            try {
                direct = await AudioFetcher.Fetch(url, !enqueue);
            } catch (Exception e) {
                Log.Error("Invalid link: {0}", e);
                error.WithDescription("The link you provided is invalid!");
                await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                    .WithContent("").AddEmbed(error.Build()));
                return;
            }

            QueueSystem.Enqueue(ctx.Guild.Id, direct, ctx.User);
            if (enqueue) {
                var pos = QueueSystem.GetQueuePosition(ctx.Guild.Id);
                var embed2 = new DiscordEmbedBuilder()
                    .WithFooter($"This song was enqueued, will be played after {pos} songs!")
                    .WithAuthor(direct.Author, 
                        iconUrl: direct.Avatar)
                    .WithImageUrl(direct.Thumbnail)
                    .WithColor(DiscordColor.Green)
                    .WithTitle(direct.Title);
                embed2.WithDescription(direct.Duration.HasValue
                    ? $"Song duration is {direct.Duration:hh\\:mm\\:ss}, suggested by {ctx.User.Mention}"
                    : $"Radio suggested by {ctx.User.Mention}");
                await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                    .WithContent("").AddEmbed(embed2.Build()));
                return;
            }
            
            var embed = new DiscordEmbedBuilder()
                .WithFooter("First song in guild's queue, will connect in a moment...")
                .WithAuthor(direct.Author, iconUrl: direct.Avatar)
                .WithImageUrl(direct.Thumbnail)
                .WithColor(DiscordColor.Yellow)
                .WithTitle(direct.Title);
            embed.WithDescription(direct.Duration.HasValue
                ? $"Song duration is {direct.Duration:hh\\:mm\\:ss}, suggested by {ctx.User.Mention}"
                : $"Radio suggested by {ctx.User.Mention}");
            await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                .WithContent("").AddEmbed(embed.Build()));
            await HandlerLoop(ctx, direct);
        } catch (Exception e) {
            Console.WriteLine("Uncaught exception in play:");
            Console.WriteLine(e);
        }
    }

    private async Task HandlerLoop(InteractionContext ctx, AudioFetcher.Result? direct) {
        var beginFrom = TimeSpan.Zero;
        var first = true;
        while (true) {
            var watch = new Stopwatch();
            QueueSystem.SetData(ctx.Guild.Id, direct!);
            var extras = QueueSystem.GetExtras(ctx.Guild.Id);
            if (extras != "") extras += " ";
            if (direct.Duration == null) {
                Log.Debug("[HandlerLoop] Forcibly reset beginFrom: song has no duration (radio)");
                beginFrom = TimeSpan.Zero;
            }
            Log.Debug("[HandlerLoop] Starting playback from {0} ({1})", 
                beginFrom, direct.Title);
            var param = beginFrom != TimeSpan.Zero ? $"-ss {beginFrom.TotalSeconds}" : "";
            var arguments =
                $"-hide_banner -loglevel panic -i {direct.URL} {param} -ac 2 -f s16le {extras}-ar 48000 pipe:1";
            Log.Debug("[HandlerLoop] FFMPEG arguments are \"{0}\"", arguments);
            var ffmpeg = Process.Start(new ProcessStartInfo {
                FileName = "ffmpeg",
                Arguments = arguments,
                RedirectStandardOutput = true,
                UseShellExecute = false
            });
            var pcm = ffmpeg!.StandardOutput.BaseStream;
            var token = QueueSystem.GetToken(ctx.Guild.Id);
            var firstByte = (byte)pcm.ReadByte();
            if (first) {
                var embed = new DiscordEmbedBuilder()
                    .WithFooter("First song in guild's queue, connected to voice!")
                    .WithAuthor(direct.Author, iconUrl: direct.Avatar)
                    .WithImageUrl(direct.Thumbnail)
                    .WithColor(DiscordColor.Green)
                    .WithTitle(direct.Title);
                embed.WithDescription(direct.Duration.HasValue
                    ? $"Song duration is {direct.Duration:hh\\:mm\\:ss}, suggested by {ctx.User.Mention}"
                    : $"Radio suggested by {ctx.User.Mention}");
                await ctx.EditResponseAsync(new DiscordWebhookBuilder()
                    .WithContent("").AddEmbed(embed.Build()));
                first = false;
            }
            var existing = ctx.Client.GetVoiceNext().GetConnection(ctx.Guild);
            VoiceNextConnection connection;
            if (existing == null) // it can be null stfu lmao
                connection = await ctx.Member.VoiceState.Channel.ConnectAsync();
            else connection = existing;
            var transmit = connection.GetTransmitSink();
            var repeat = true; var forceRepeat = false;
            var resetBeginFrom = true;
            try {
                watch.Start();
                // ReSharper disable once MethodSupportsCancellation
                await transmit.WriteAsync(new[] { firstByte }, 0, 1);
                var terminator = new CancellationTokenSource();
                var copyTask = CopyHandler();
                async Task ActionHandler() {
                    while (copyTask.Status != TaskStatus.RanToCompletion) {
                        var current = ctx.Client.GetVoiceNext().GetConnection(ctx.Guild);
                        if (current == null) {
                            Log.Debug("[HandlerLoop] Forceful disconnect detected");
                            var embed = new DiscordEmbedBuilder()
                                .WithDescription("Why are you disconnecting me? How rude!")
                                .WithColor(DiscordColor.Yellow)
                                .WithTitle("Goober's Music | Queue cleared");
                            var last = QueueSystem.GetLastChannel(ctx.Guild.Id);
                            await last.SendMessageAsync(new DiscordMessageBuilder()
                                .WithContent("").AddEmbed(embed.Build()));
                            QueueSystem.ClearQueue(ctx.Guild.Id);
                            terminator.Cancel();
                            repeat = false;
                            return;
                        }
                        
                        if (token.IsCancellationRequested) {
                            Log.Debug("[HandlerLoop] Cancellation token triggered ({0})", direct.Title);
                            switch (QueueSystem.GetAction(ctx.Guild.Id)) {
                                case QueueSystem.ActionEnum.RefreshParameters:
                                    Log.Debug("[HandlerLoop] Refreshing FFMPEG extra parameters");
                                    watch.Stop(); // Account for prev too
                                    beginFrom = watch.Elapsed + beginFrom;
                                    resetBeginFrom = false;
                                    forceRepeat = true;
                                    terminator.Cancel();
                                    return;
                                case QueueSystem.ActionEnum.SkipCurrentSong:
                                    Log.Debug("[HandlerLoop] Skipping current song");
                                    terminator.Cancel();
                                    repeat = false;
                                    return;
                                case QueueSystem.ActionEnum.StartFromBeginning:
                                    Log.Debug("[HandlerLoop] Starting from beginning");
                                    forceRepeat = true;
                                    terminator.Cancel();
                                    return;
                                case QueueSystem.ActionEnum.PausePlayback:
                                    Log.Debug("[HandlerLoop] Pausing playback");
                                    terminator.Cancel();
                                    while (true) {
                                        var action = QueueSystem.GetAction(ctx.Guild.Id);
                                        if (action != QueueSystem.ActionEnum.ResumePlayback) {
                                            await Task.Delay(500);
                                            continue;
                                        }
                        
                                        beginFrom = watch.Elapsed + beginFrom;
                                        resetBeginFrom = false;
                                        forceRepeat = true;
                                        break;
                                    }
                                    break;
                            }
                        }

                        await Task.Delay(500);
                    }
                }

                async Task CopyHandler() {
                    try {
                        await pcm.CopyToAsync(transmit, cancellationToken: terminator.Token);
                        await connection.WaitForPlaybackFinishAsync();
                    } catch { /* Don't crash when cancellation token got triggered */ }
                }
                
                var actionHandler = ActionHandler();
                await Task.WhenAll(copyTask, actionHandler);
                if (resetBeginFrom) beginFrom = TimeSpan.Zero;
            } catch (Exception e) {
                Console.WriteLine(e);
            }
            await pcm.DisposeAsync();
            if ((QueueSystem.GetRepeat(ctx.Guild.Id) && repeat) || forceRepeat) {
                direct = await AudioFetcher.Fetch(direct.Original);
                continue;
            }
            
            QueueSystem.Dequeue(ctx.Guild.Id);
            QueueSystem.SetExtras(ctx.Guild.Id, "");
            direct = await QueueSystem.GetSong(ctx.Guild.Id);
            if (direct != null) continue;
            QueueSystem.Stopped(ctx.Guild.Id);
            connection.Disconnect();
            return;
        }
    }

    [SlashCommand("queue", "List songs currently in queue")]
    public async Task QueueCommand(InteractionContext ctx,
        [Option("page", "Page number to show")] long page = 1) {
        var verify = await Verify(ctx, true, false);
        if (!verify) return;

        if (page < 1) page = 1;
        try {
            var queue = QueueSystem.GetEntireQueue(ctx.Guild.Id);
            var pages = queue.Chunk(5).ToList();
            var embed = new DiscordEmbedBuilder()
                .WithColor(DiscordColor.Green)
                .WithTitle("Goober's Music | Current queue")
                .WithFooter($"Page {page} out of {pages.Count}");
            Console.WriteLine(pages.Count);
            foreach (var song in pages[(int)page - 1]) embed.AddField(song.Title, 
                $"Suggested by {song.SuggestedBy.Mention}");
            await ctx.CreateResponseAsync(new DiscordInteractionResponseBuilder()
                .AddEmbed(embed));
        } catch (Exception e) {
            Log.Debug("Failed to paginate: {0}", e);
            await ctx.CreateResponseAsync(new DiscordInteractionResponseBuilder()
                .AddEmbed(new DiscordEmbedBuilder()
                    .WithColor(DiscordColor.Red)
                    .WithTitle("Goober's Music | Error occured")
                    .WithDescription("This page doesn't exist!")
                    .Build()));
        }
    }
    
    [SlashCommand("current", "Tells you what song is currently playing")]
    public async Task CurrentCommand(InteractionContext ctx) {
        var verify = await Verify(ctx, true, false);
        if (!verify) return;
        
        var direct = QueueSystem.GetCurrentSong(ctx.Guild.Id);
        var embed = new DiscordEmbedBuilder()
            .WithAuthor(direct!.Author, iconUrl: direct.Avatar)
            .WithImageUrl(direct.Thumbnail)
            .WithColor(DiscordColor.Green)
            .WithTitle(direct.Title);
        embed.WithDescription(direct.Duration.HasValue
            ? $"Song duration is {direct.Duration:hh\\:mm\\:ss}, suggested by {direct.SuggestedBy.Mention}"
            : $"Radio suggested by {direct.SuggestedBy.Mention}");
        await ctx.CreateResponseAsync(new DiscordInteractionResponseBuilder()
            .WithContent("").AddEmbed(embed.Build()));
    }
    
    [SlashCommand("skip", "Skips currently playing song")]
    public async Task SkipCommand(InteractionContext ctx) {
        var verify = await Verify(ctx, true);
        if (!verify) return;

        var previous = QueueSystem.GetCurrentSong(ctx.Guild.Id);
        QueueSystem.DoAction(ctx.Guild.Id, QueueSystem.ActionEnum.SkipCurrentSong);
        var direct = QueueSystem.Peek(ctx.Guild.Id);
        direct ??= new QueueSystem.Song {
            Author = "No song up next",
            Avatar = "https://media.tenor.com/damu8hbJ19YAAAAC/shrug-emoji.gif",
            Title = "Goober's Music | Skip current song",
            SuggestedBy = ctx.User,
            Thumbnail = ""
        };
        var embed = new DiscordEmbedBuilder()
            .WithFooter($"Skipped {previous.Title}")
            .WithAuthor(direct.Author, iconUrl: direct.Avatar)
            .WithImageUrl(direct.Thumbnail)
            .WithColor(DiscordColor.Green)
            .WithTitle(direct.Title);
        embed.WithDescription(direct.Duration.HasValue
            ? $"Song duration is {direct.Duration:hh\\:mm\\:ss}, suggested by {direct.SuggestedBy.Mention}"
            : $"Radio suggested by {direct.SuggestedBy.Mention}");
        await ctx.CreateResponseAsync(new DiscordInteractionResponseBuilder()
            .WithContent("").AddEmbed(embed.Build()));
    }
    
    [SlashCommand("pause", "Pauses currently playing song")]
    public async Task PauseCommand(InteractionContext ctx) {
        var verify = await Verify(ctx, true);
        if (!verify) return;
        
        QueueSystem.DoAction(ctx.Guild.Id, QueueSystem.ActionEnum.PausePlayback);
        var direct = QueueSystem.GetCurrentSong(ctx.Guild.Id);
        var embed = new DiscordEmbedBuilder()
            .WithFooter("Successfully paused this song.")
            .WithAuthor(direct!.Author, iconUrl: direct.Avatar)
            .WithImageUrl(direct.Thumbnail)
            .WithColor(DiscordColor.Green)
            .WithTitle(direct.Title);
        embed.WithDescription(direct.Duration.HasValue
            ? $"Song duration is {direct.Duration:hh\\:mm\\:ss}, suggested by {direct.SuggestedBy.Mention}"
            : $"Radio suggested by {direct.SuggestedBy.Mention}");
        await ctx.CreateResponseAsync(new DiscordInteractionResponseBuilder()
            .WithContent("").AddEmbed(embed.Build()));
    }
    
    [SlashCommand("resume", "Resumes currently playing song")]
    public async Task ResumeCommand(InteractionContext ctx) {
        var verify = await Verify(ctx, true);
        if (!verify) return;
        
        QueueSystem.DoAction(ctx.Guild.Id, QueueSystem.ActionEnum.ResumePlayback);
        var direct = QueueSystem.GetCurrentSong(ctx.Guild.Id);
        var embed = new DiscordEmbedBuilder()
            .WithFooter("Successfully resumed this song.")
            .WithAuthor(direct!.Author, iconUrl: direct.Avatar)
            .WithImageUrl(direct.Thumbnail)
            .WithColor(DiscordColor.Green)
            .WithTitle(direct.Title);
        embed.WithDescription(direct.Duration.HasValue
            ? $"Song duration is {direct.Duration:hh\\:mm\\:ss}, suggested by {direct.SuggestedBy.Mention}"
            : $"Radio suggested by {direct.SuggestedBy.Mention}");
        await ctx.CreateResponseAsync(new DiscordInteractionResponseBuilder()
            .WithContent("").AddEmbed(embed.Build()));
    }
    
    [SlashCommand("restart", "Restarts currently playing song")]
    public async Task RestartCommand(InteractionContext ctx) {
        var verify = await Verify(ctx, true);
        if (!verify) return;

        QueueSystem.DoAction(ctx.Guild.Id, QueueSystem.ActionEnum.StartFromBeginning);
        var direct = QueueSystem.GetCurrentSong(ctx.Guild.Id);
        var embed = new DiscordEmbedBuilder()
            .WithFooter("Restarted song playback from the beginning")
            .WithAuthor(direct!.Author, iconUrl: direct.Avatar)
            .WithImageUrl(direct.Thumbnail)
            .WithColor(DiscordColor.Green)
            .WithTitle(direct.Title);
        embed.WithDescription(direct.Duration.HasValue
            ? $"Song duration is {direct.Duration:hh\\:mm\\:ss}, suggested by {direct.SuggestedBy.Mention}"
            : $"Radio suggested by {direct.SuggestedBy.Mention}");
        await ctx.CreateResponseAsync(new DiscordInteractionResponseBuilder()
            .WithContent("").AddEmbed(embed.Build()));
    }
    
    [SlashCommand("volume", "Changes playback volume")]
    public async Task VolumeCommand(InteractionContext ctx,
        [Option("volume", "Volume from 0% to 250%")] double volume) {
        var verify = await Verify(ctx, true);
        if (!verify) return;
        
        var current = ctx.Client.GetVoiceNext().GetConnection(ctx.Guild);
        current.GetTransmitSink().VolumeModifier = Math.Clamp(volume / 100, 0, 1);
        var direct = QueueSystem.GetCurrentSong(ctx.Guild.Id);
        var embed = new DiscordEmbedBuilder()
            .WithFooter("Set playback volume to " + volume)
            .WithAuthor(direct!.Author, iconUrl: direct.Avatar)
            .WithImageUrl(direct.Thumbnail)
            .WithColor(DiscordColor.Green)
            .WithTitle(direct.Title);
        embed.WithDescription(direct.Duration.HasValue
            ? $"Song duration is {direct.Duration:hh\\:mm\\:ss}, suggested by {direct.SuggestedBy.Mention}"
            : $"Radio suggested by {direct.SuggestedBy.Mention}");
        await ctx.CreateResponseAsync(new DiscordInteractionResponseBuilder()
            .WithContent("").AddEmbed(embed.Build()));
    }
    
    [SlashCommand("nightcore", "Enables nightcore for currently playing song")]
    public async Task NightcoreCommand(InteractionContext ctx) {
        var verify = await Verify(ctx, true);
        if (!verify) return;

        var current = QueueSystem.GetCurrentSong(ctx.Guild.Id);
        Console.WriteLine(current.Avatar);
        QueueSystem.SetExtras(ctx.Guild.Id, "-af aresample=48000,asetrate=48000*1.25"); // Bassboost filter
        QueueSystem.DoAction(ctx.Guild.Id, QueueSystem.ActionEnum.RefreshParameters);
        var embed = new DiscordEmbedBuilder()
            .WithDescription("Applied nightcore filter: `-af aresample=48000,asetrate=48000*1.25`")
            .WithAuthor(current.Author, iconUrl: current.Avatar)
            .WithColor(DiscordColor.Green)
            .WithTitle(current.Title);
        await ctx.CreateResponseAsync(new DiscordInteractionResponseBuilder()
            .WithContent("").AddEmbed(embed.Build()));
    }
    
    [SlashCommand("bassboost", "Enables bassboost for currently playing song")]
    public async Task BassboostCommand(InteractionContext ctx,
        [Option("amount", "How much bassboost to apply in dB")] double db) {
        var verify = await Verify(ctx, true);
        if (!verify) return;

        var current = QueueSystem.GetCurrentSong(ctx.Guild.Id);
        var filter = $"-af bass=g={db.ToString().Split(".")[0]}";
        QueueSystem.SetExtras(ctx.Guild.Id, filter); // Bassboost filter
        QueueSystem.DoAction(ctx.Guild.Id, QueueSystem.ActionEnum.RefreshParameters);
        var embed = new DiscordEmbedBuilder()
            .WithDescription($"Applied bassboost filter: `{filter}`")
            .WithAuthor(current.Author, iconUrl: current.Avatar)
            .WithColor(DiscordColor.Green)
            .WithTitle(current.Title);
        await ctx.CreateResponseAsync(new DiscordInteractionResponseBuilder()
            .WithContent("").AddEmbed(embed.Build()));
    }
    
    [SlashCommand("repeat", "Enable song repeat for currently playing song")]
    public async Task RepeatCommand(InteractionContext ctx) {
        var verify = await Verify(ctx, true);
        if (!verify) return;
        
        var error = new DiscordEmbedBuilder()
            .WithDescription(QueueSystem.ToggleRepeat(ctx.Guild.Id)
                ? "Enabled repeat successfully."
                : "Disabled repeat successfully.")
            .WithColor(DiscordColor.Green)
            .WithTitle("Goober's Music | Repeat");
        await ctx.CreateResponseAsync(new DiscordInteractionResponseBuilder()
            .WithContent("").AddEmbed(error.Build()));
    }
    
    [SlashCommand("filterReset", "Resets all filters for currently playing song")]
    public async Task FilterResetCommand(InteractionContext ctx) {
        var verify = await Verify(ctx, true);
        if (!verify) return;

        var current = QueueSystem.GetCurrentSong(ctx.Guild.Id);
        QueueSystem.SetExtras(ctx.Guild.Id, ""); // Reset all filters
        QueueSystem.DoAction(ctx.Guild.Id, QueueSystem.ActionEnum.RefreshParameters);
        var embed = new DiscordEmbedBuilder()
            .WithDescription("Set current filter to empty")
            .WithAuthor(current.Author, iconUrl: current.Avatar)
            .WithColor(DiscordColor.Green)
            .WithTitle(current.Title);
        await ctx.CreateResponseAsync(new DiscordInteractionResponseBuilder()
            .WithContent("").AddEmbed(embed.Build()));
    }

    [SlashCommand("stop", "Stops audio playback in current guild")]
    public async Task StopCommand(InteractionContext ctx) {
        var verify = await Verify(ctx, true);
        if (!verify) return;
        
        QueueSystem.ClearQueue(ctx.Guild.Id); // Wipe out the entire queue
        QueueSystem.DoAction(ctx.Guild.Id, QueueSystem.ActionEnum.SkipCurrentSong);
        QueueSystem.Stopped(ctx.Guild.Id); // No playback active lol
        var embed = new DiscordEmbedBuilder()
            .WithDescription("Stopped playback in this guild successfully.")
            .WithColor(DiscordColor.Green)
            .WithTitle("Goober's Music | Queue cleared");
        await ctx.CreateResponseAsync(new DiscordInteractionResponseBuilder()
            .WithContent("").AddEmbed(embed.Build()));
    }

    private async Task<bool> Verify(InteractionContext ctx, bool shouldExist = false, bool inVoice = true) {
        if (inVoice && ctx.Member.VoiceState == null) {
            var error = new DiscordEmbedBuilder()
                .WithDescription("You must be in a voice channel to do this!")
                .WithColor(DiscordColor.Red)
                .WithTitle("Goober's Music | Error occured");
            await ctx.CreateResponseAsync(new DiscordInteractionResponseBuilder()
                .WithContent("").AddEmbed(error.Build()));
            return false;
        }

        var existing = ctx.Client.GetVoiceNext().GetConnection(ctx.Guild);
        if (inVoice && existing != null && ctx.Member.VoiceState.Channel.Id != existing.TargetChannel.Id) {
            var error = new DiscordEmbedBuilder()
                .WithDescription($"You must be in {existing.TargetChannel.Mention} to do this!")
                .WithColor(DiscordColor.Red)
                .WithTitle("Goober's Music | Error occured");
            await ctx.CreateResponseAsync(new DiscordInteractionResponseBuilder()
                .WithContent("").AddEmbed(error.Build()));
            return false;
        }

        var success = existing != null || !shouldExist;
        if (success) {
            QueueSystem.ForceAllow(ctx.Guild.Id); 
            QueueSystem.SetLastChannel(ctx.Guild.Id, ctx.Channel);
            return true;
        }
        
        var nou = new DiscordEmbedBuilder()
            .WithDescription("I ain't playing shit rn!")
            .WithColor(DiscordColor.Red)
            .WithTitle("Goober's Music | Error occured");
        await ctx.CreateResponseAsync(new DiscordInteractionResponseBuilder()
            .WithContent("").AddEmbed(nou.Build()));
        return false;
    }
}
