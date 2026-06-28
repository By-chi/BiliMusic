using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HttpClient = System.Net.Http.HttpClient;
public class LrclibSource : ILyricsSource
{
    private const string SearchUrl = "https://lrclib.net/api/search";
    private readonly HttpClient _http;

    public LrclibSource(HttpClient http)
    {
        _http = http;
    }

    public async Task<List<SongInfo>> SearchAsync(string keyword)
    {
        string url = $"{SearchUrl}?track_name={Uri.EscapeDataString(keyword)}";
        try
        {
            string json = await _http.GetStringAsync(url);
            var results = JsonSerializer.Deserialize<List<LrclibResult>>(json,
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

    public async Task<string> GetLyricAsync(SongInfo song)
    {
        string trackName = song.Name;
        string artistName = song.Artist;
        string url = $"{SearchUrl}?track_name={Uri.EscapeDataString(trackName)}&artist_name={Uri.EscapeDataString(artistName ?? "")}";
        try
        {
            string json = await _http.GetStringAsync(url);
            var results = JsonSerializer.Deserialize<List<LrclibResult>>(json,
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