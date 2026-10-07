using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HttpClient = System.Net.Http.HttpClient;

/// <summary>
/// Lrclib 歌词源（https://lrclib.net/api/search）
/// </summary>
public class LrclibSource(HttpClient http) : LyricsHttpSourceBase(http)
{
    private const string SearchUrl = "https://lrclib.net/api/search";

    protected override string SourceName => "LRCLIB";

    public override async Task<List<SongInfo>> SearchAsync(string keyword)
    {
        string url = $"{SearchUrl}?track_name={Uri.EscapeDataString(keyword)}";
        using var doc = await GetJsonAsync(url);
        if (doc == null)
            return null;

        try
        {
            var results = JsonSerializer.Deserialize<List<LrclibResult>>(doc.RootElement.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (results == null || results.Count == 0) return null;

            return results.Select(r => new SongInfo
            {
                Id = $"{r.TrackName}|{r.ArtistName}",
                Name = r.TrackName,
                Artist = r.ArtistName
            }).ToList();
        }
        catch (Exception e)
        {
            GD.PrintErr($"[LRCLIB Search] {e.Message}");
            return null;
        }
    }

    public override async Task<string> GetLyricAsync(SongInfo song)
    {
        string trackName = song.Name;
        string artistName = song.Artist;
        string url = $"{SearchUrl}?track_name={Uri.EscapeDataString(trackName)}&artist_name={Uri.EscapeDataString(artistName ?? "")}";
        using var doc = await GetJsonAsync(url);
        if (doc == null)
            return null;

        try
        {
            var results = JsonSerializer.Deserialize<List<LrclibResult>>(doc.RootElement.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (results == null || results.Count == 0) return null;
            var best = results.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.SyncedLyrics));
            return best?.SyncedLyrics ?? results.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.PlainLyrics))?.PlainLyrics;
        }
        catch (Exception e)
        {
            GD.PrintErr($"[LRCLIB Exact] {e.Message}");
            return null;
        }
    }

    private class LrclibResult
    {
        public string TrackName { get; set; }
        public string ArtistName { get; set; }
        public string PlainLyrics { get; set; }
        public string SyncedLyrics { get; set; }
    }
}
