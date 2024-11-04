using System.Diagnostics;
using System.IO.Enumeration;
using System.Net;
using System.Web;
using System.Xml;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Newtonsoft.Json.Linq;

namespace MusicDemo; 

/// <summary>
/// Fetches direct audio link from a public URL
/// Currently supports:
/// - Direct links
/// - SoundCloud
/// - BandCamp
/// - Youtube
/// </summary>
public class AudioFetcher {
    /// <summary>
    /// Metadata + URL
    /// </summary>
    public class Result {
        public string Original { get; set; }
        public string? URL { get; set; }
        public string Title { get; set; }
        public string Author { get; set; }
        public string Avatar { get; set; }
        public string Thumbnail { get; set; }
        public TimeSpan? Duration { get; set; }
    }
    
    /// <summary>
    /// Handler interface
    /// </summary>
    private interface Handler {
        public Task<Result> GetUrl(string url, bool doLink);
        public string[] MatchCase();
    }
    
    /// <summary>
    /// SoundCloud handler
    /// </summary>
    private class SoundCloud : Handler {
        public async Task<Result> GetUrl(string url, bool doLink) {
            var orig = url;
            // Fetch the raw HTML of the web page
            var client = new HttpClient();
            var res = await client.GetAsync(url);
            var content = await res.Content.ReadAsStringAsync();
            // Get the JSON using questionable methods
            var start = "<script>window.__sc_hydration = ";
            var json = content.Split(start)[1];
            json = json.Split(";</script>")[0];
            dynamic obj = JArray.Parse(json);
            foreach (var item in obj) {
                // Check if it's the "sound" hydratable
                if (item.hydratable != "sound") continue;
                if (!doLink)
                    return new Result {
                        URL = "",
                        Author = item.data.user.username,
                        Avatar = item.data.user.avatar_url,
                        Duration = TimeSpan.FromMilliseconds((double)item.data.duration),
                        Thumbnail = item.data.artwork_url,
                        Title = item.data.title,
                        Original = orig
                    };
                // Just do some JSON traversal and here's our link!
                var req = (string)(item.data.media.transcodings[0].url
                                   + $"?track_authorization={item.data.track_authorization}"
                                   + "&client_id=zy0ijES9ACCAxntrQj4MN4wKRlluii0I");
                // We still have to do tomfoolery to get m3u8
                var res2 = await client.GetAsync(req);
                var content2 = await res2.Content.ReadAsStringAsync();
                dynamic json2 = JObject.Parse(content2);
                return new Result {
                    URL = json2.url,
                    Author = item.data.user.username,
                    Avatar = item.data.user.avatar_url,
                    Duration = TimeSpan.FromMilliseconds((double)item.data.duration),
                    Thumbnail = item.data.artwork_url,
                    Title = item.data.title,
                    Original = orig
                };
            }

            throw new Exception("Unknown failure: failed to parse hydration!");
        }

        public string[] MatchCase()
            => new [] { "https://soundcloud.com/*" };
    }
    
    /// <summary>
    /// BandCamp handler
    /// </summary>
    private class BandCamp : Handler {
        public async Task<Result> GetUrl(string url, bool doLink) {
            // Fetch the raw HTML of the web page
            var client = new HttpClient();
            var res = await client.GetAsync(url);
            var content = await res.Content.ReadAsStringAsync();
            content = HttpUtility.HtmlDecode(content);
            // Get the URL using questionable methods
            string? direct = "";
            if (doLink) {
                var ffmpeg = Process.Start(new ProcessStartInfo {
                    FileName = "yt-dlp",
                    Arguments = "-g -f bestaudio " + url,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                })!;
                direct = await ffmpeg.StandardOutput.ReadLineAsync();
            }
            // Get the JSON using questionable methods
            var json = content.Split("<script type=\"application/ld+json\">")[1];
            json = json.Split("</script>")[0];
            dynamic item = JObject.Parse(json);
            var ffprobe = Process.Start(new ProcessStartInfo {
                FileName = "ffprobe",
                Arguments = "-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 " + direct,
                UseShellExecute = false,
                RedirectStandardOutput = true
            })!;
            var sec = (await ffprobe.StandardOutput.ReadLineAsync())!;
            return new Result {
                URL = direct,
                Author = item.publisher.name,
                Avatar = item.publisher.image,
                Duration = TimeSpan.FromSeconds(double.Parse(sec)),
                Thumbnail = item.image,
                Title = item.name,
                Original = url
            };
        }

        public string[] MatchCase()
            => new [] { "https://*.bandcamp.com/track/*" };
    }
    
    /// <summary>
    /// YouTube handler
    /// </summary>
    private class YouTube : Handler {
        public async Task<Result> GetUrl(string url, bool doLink) {
            url = url.Replace("https://youtu.be/", 
                "https://www.youtube.com/watch?v=");
            url = url.Split("&")[0];
            string? direct = "";
            if (doLink) {
                var ffmpeg = Process.Start(new ProcessStartInfo {
                    FileName = "yt-dlp",
                    Arguments = "-g -f bestaudio " + url,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                })!;
                direct = (await ffmpeg.StandardOutput.ReadLineAsync())!;
            }
            using var api = new YouTubeService(new BaseClientService.Initializer {
                ApiKey = "AIzaSyBzhVDFeIW0mhem-J3o_9ZO8OOB3Yo9LZI"
            });
            var req = api.Videos.List("snippet");
            req.Id = url.Split("https://www.youtube.com/watch?v=")[1];
            var res = await req.ExecuteAsync();
            var video = res.Items.FirstOrDefault();
            var req2 = api.Channels.List("snippet");
            req2.Id = video!.Snippet.ChannelId;
            var res2 = await req2.ExecuteAsync();
            var channel = res2.Items.FirstOrDefault();
            var req3 = api.Videos.List("contentDetails");
            req3.Id = url.Split("https://www.youtube.com/watch?v=")[1];
            var res3 = await req3.ExecuteAsync();
            var det = res3.Items.FirstOrDefault();
            return new Result {
                URL = direct,
                Author = video.Snippet.ChannelTitle,
                Avatar = channel.Snippet.Thumbnails.Default__.Url,
                Duration = XmlConvert.ToTimeSpan(det.ContentDetails.Duration),
                Thumbnail = video.Snippet.Thumbnails.Default__.Url,
                Title = video.Snippet.Title,
                Original = url
            };
        }

        public string[] MatchCase()
            => new [] {
                "https://www.youtube.com/watch?v=*",
                "https://youtu.be/*"
            };
    }

    /// <summary>
    /// List of all handlers
    /// </summary>
    private static Handler[] _handlers = {
        new SoundCloud(), new YouTube(), new BandCamp()
    };

    /// <summary>
    /// Fetch the direct audio link and metadata
    /// </summary>
    /// <param name="url">Public URL</param>
    /// <param name="doLink">Toggle direct link</param>
    /// <returns>Direct audio only URL</returns>
    public static async Task<Result> Fetch(string url, bool doLink = true) {
        foreach (var i in _handlers) {
            var found = false;
            foreach (var j in i.MatchCase())
                if (FileSystemName.MatchesSimpleExpression(
                        j, url)) found = true;
            if (!found) continue;
            return await i.GetUrl(url, doLink);
        }

        // In case of a direct link
        var ffprobe = Process.Start(new ProcessStartInfo {
            FileName = "ffprobe",
            Arguments = "-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 " + url,
            UseShellExecute = false,
            RedirectStandardOutput = true
        })!;
        var sec = (await ffprobe.StandardOutput.ReadLineAsync())!;
        return new Result {
            URL = url,
            Avatar = "https://media.tenor.com/damu8hbJ19YAAAAC/shrug-emoji.gif",
            Author = "Unknown author",
            Thumbnail = "",
            Duration = sec == "N/A" ? null : TimeSpan.FromSeconds(double.Parse(sec)),
            Title = url,
            Original = url
        };
    }
}