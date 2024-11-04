using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.SlashCommands;
using DSharpPlus.VoiceNext;
using Microsoft.Extensions.Logging;
using System.IO;
using MusicDemo;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .CreateLogger();
var discord = new DiscordShardedClient(new DiscordConfiguration {
    Token = File.ReadAllText("token.txt") /* the token totally wasn't hardcoded before */,
    LoggerFactory = new LoggerFactory().AddSerilog(),
    Intents = DiscordIntents.All,
    TokenType = TokenType.Bot
});

discord.VoiceStateUpdated += async (_, e) => {
    if (!QueueSystem.ShouldEnqueue(e.Guild.Id)) return;
    var channel = e.Before == null ? e.After.Channel : e.Before.Channel;
    if (channel.Users.Count != 1 || channel.Users[0].Id 
        != discord.CurrentUser.Id) return;
    QueueSystem.ClearQueue(e.Guild.Id); // Wipe out the entire queue
    QueueSystem.DoAction(e.Guild.Id, QueueSystem.ActionEnum.SkipCurrentSong);
    QueueSystem.Stopped(e.Guild.Id); // No playback active lol
    var embed = new DiscordEmbedBuilder()
        .WithDescription($"Everyone left {channel.Mention}, so did I!")
        .WithColor(DiscordColor.Yellow)
        .WithTitle("Goober's Music | Queue cleared");
    var last = QueueSystem.GetLastChannel(e.Guild.Id);
    await last.SendMessageAsync(new DiscordMessageBuilder()
        .WithContent("").AddEmbed(embed.Build()));
};

Log.Information("[BootUp] Starting up VoiceNext...");
await discord.UseVoiceNextAsync(
    new VoiceNextConfiguration());

Log.Information("[BootUp] Enabling slash commands...");
(await discord.UseSlashCommandsAsync())
    .RegisterCommands<SlashCommands>();

Log.Information("[BootUp] Starting up the bot...");
await discord.StartAsync();
Log.Information("[BootUp] Ready to accept commands!");
await Task.Delay(-1);
