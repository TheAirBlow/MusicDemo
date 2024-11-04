using DSharpPlus.Entities;

namespace MusicDemo; 

public class QueueSystem {
    public enum ActionEnum {
        /// <summary>
        /// Starts playback from beginning
        /// </summary>
        StartFromBeginning,
        
        /// <summary>
        /// Refreshes ffmpeg extra parameters
        /// </summary>
        RefreshParameters,
        
        /// <summary>
        /// Skips the current song
        /// </summary>
        SkipCurrentSong,
        
        /// <summary>
        /// Pauses playback
        /// </summary>
        PausePlayback,
        
        /// <summary>
        /// Resumes playback
        /// </summary>
        ResumePlayback
    }

    public class Song {
        public string URL { get; set; }
        public string Title { get; set; }
        public string Author { get; set; }
        public string Avatar { get; set; }
        public string Thumbnail { get; set; }
        public DiscordUser SuggestedBy { get; set; }
        public TimeSpan? Duration { get; set; }
    }
    
    public class DictionaryElement {
        public CancellationTokenSource Token = new();
        public AudioFetcher.Result? CurrentSong;
        public DiscordChannel? LastChannel;
        public bool CurrentlyPlaying = false;
        public bool RepeatCurrent = false;
        public string FfmpegExtras = "";
        public List<Song> Queue = new();
        public ActionEnum Action;
    }
    
    private static Dictionary<ulong, DictionaryElement> _queue = new ();

    public static void Enqueue(ulong guild, AudioFetcher.Result song, DiscordUser by) {
        if (!_queue.ContainsKey(guild))
            _queue.Add(guild, new DictionaryElement());
        _queue[guild].Queue.Add(new Song {
            Author = song.Author,
            Avatar = song.Avatar,
            Duration = song.Duration,
            Thumbnail = song.Thumbnail,
            Title = song.Title,
            URL = song.Original,
            SuggestedBy = by
        });
    }

    public static async Task<AudioFetcher.Result?> GetSong(ulong guild) {
        if (!_queue.ContainsKey(guild)) return null;
        if (_queue[guild].Queue.Count == 0) return null;
        var item = _queue[guild].Queue[0];
        return await AudioFetcher.Fetch(item.URL);
    }

    public static bool IsDuplicate(ulong guild, string url) {
        if (!_queue.ContainsKey(guild)) return false;
        foreach (var i in _queue[guild].Queue)
            if (i.URL == url) return true;
        return false;
    }

    public static int GetQueuePosition(ulong guild)
        => _queue[guild].Queue.Count - 1;

    public static Song? Peek(ulong guild) {
        try {
            if (!_queue.ContainsKey(guild)) return null;
            if (_queue[guild].Queue.Count == 0) return null;
            var item = _queue[guild].Queue[1];
            return item;  
        } catch {
            /* L + ratio + stfu */
            return null;
        }
    }
    
    public static void Dequeue(ulong guild) {
        try { _queue[guild].Queue.RemoveAt(0); }
        catch { /* L + ratio + stfu */ }
    }
    
    public static void ForceAllow(ulong guild) {
        try { _queue.Add(guild, new DictionaryElement()); }
        catch { /* L + ratio + stfu */ }
    }

    public static bool ShouldEnqueue(ulong guild)
        => _queue.ContainsKey(guild) && _queue[guild].CurrentlyPlaying;

    public static void ClearQueue(ulong guild)
        => _queue[guild].Queue.Clear();

    public static void DoAction(ulong guild, ActionEnum action) {
        _queue[guild].Action = action;
        _queue[guild].Token.Cancel();
        // Refreshes the token cause this one is dead now
        _queue[guild].Token = new CancellationTokenSource();
    }

    public static List<Song> GetEntireQueue(ulong guild)
        => _queue[guild].Queue;

    public static ActionEnum GetAction(ulong guild)
        => _queue[guild].Action;

    public static Song GetCurrentSong(ulong guild)
        => _queue[guild].Queue[0];

    public static CancellationToken GetToken(ulong guild)
        => _queue[guild].Token.Token;

    public static void Stopped(ulong guild)
        => _queue[guild].CurrentlyPlaying = false;

    public static void SetData(ulong guild, AudioFetcher.Result data) {
        _queue[guild].CurrentlyPlaying = true;
        _queue[guild].CurrentSong = data;
    }

    public static void SetExtras(ulong guild, string extras)
        => _queue[guild].FfmpegExtras = extras;

    public static string GetExtras(ulong guild)
        => _queue[guild].FfmpegExtras;
    
    public static bool ToggleRepeat(ulong guild) {
        _queue[guild].RepeatCurrent = !_queue[guild].RepeatCurrent;
        return _queue[guild].RepeatCurrent;
    }

    public static bool GetRepeat(ulong guild)
        => _queue[guild].RepeatCurrent;

    public static void SetLastChannel(ulong guild, DiscordChannel channel)
        => _queue[guild].LastChannel = channel;

    public static DiscordChannel? GetLastChannel(ulong guild)
        => _queue[guild].LastChannel;
}