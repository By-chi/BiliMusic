using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HttpClient = System.Net.Http.HttpClient;
public class WwoyunSource : ILyricsSource
{
    private const string SearchUrl = "https://zm.wwoyun.cn/search";
    private const string LyricUrl = "https://zm.wwoyun.cn/lyric";
    private readonly HttpClient _http;

    public WwoyunSource(HttpClient http) => _http = http;

    public async Task<List<SongInfo>> SearchAsync(string keyword)
    {
        string url = $"{SearchUrl}?keywords={Uri.EscapeDataString(keyword)}&limit=10&type=1";
        GD.Print($"[Wwoyun] 搜索请求: {url}");
        try
        {
            string json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("code").GetInt32() != 200)
            {
                GD.Print($"[Wwoyun] 搜索失败，返回 code: {doc.RootElement}");
                return null;
            }
            var songsElem = doc.RootElement.GetProperty("result").GetProperty("songs");
            var songs = new List<SongInfo>();
            foreach (var item in songsElem.EnumerateArray())
            {
                songs.Add(new SongInfo
                {
                    Id = item.GetProperty("id").GetInt64().ToString(),
                    Name = item.GetProperty("name").GetString(),
                    Artist = item.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0
                             ? artists[0].GetProperty("name").GetString() : ""
                });
            }
            GD.Print($"[Wwoyun] 搜索成功，找到 {songs.Count} 首候选");
            return songs;
        }
        catch (Exception e)
        {
            GD.PrintErr($"[Wwoyun Search] 异常: {e.Message}");
            return null;
        }
    }

    public async Task<string> GetLyricAsync(SongInfo song)
    {
        string url = $"{LyricUrl}?id={song.Id}";
        GD.Print($"[Wwoyun] 获取歌词请求: {url}");
        try
        {
            string json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("code").GetInt32() != 200)
            {
                GD.Print($"[Wwoyun] 获取歌词失败，返回 code: {doc.RootElement}");
                return null;
            }
            string lyric = doc.RootElement.GetProperty("lrc").GetProperty("lyric").GetString();
            if (string.IsNullOrEmpty(lyric))
            {
                GD.Print("[Wwoyun] 歌词内容为空");
                return null;
            }
            GD.Print($"[Wwoyun] 歌词获取成功，长度: {lyric.Length} 字符");
            return lyric;
        }
        catch (Exception e)
        {
            GD.PrintErr($"[Wwoyun Lyric] 异常: {e.Message}");
            return null;
        }
    }
}