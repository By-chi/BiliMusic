using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HttpClient = System.Net.Http.HttpClient;

/// <summary>
/// Oiapi.net QQ音乐歌词源（基于 https://www.oiapi.net/api/QQMusicLyric）
/// </summary>
public class OiapiSource : LyricsHttpSourceBase
{
    private const string ApiUrl = "https://www.oiapi.net/api/QQMusicLyric";

    public OiapiSource(HttpClient http) : base(http) { }

    protected override string SourceName => "Oiapi";

    public override async Task<List<SongInfo>> SearchAsync(string keyword)
    {
        string url = $"{ApiUrl}?keyword={Uri.EscapeDataString(keyword)}&limit=10";
        GD.Print($"[Oiapi] 搜索请求: {url}");
        using var doc = await GetJsonAsync(url);
        if (doc == null) return null;

        // 文档中 code=1 表示成功
        if (GetRootCode(doc) != 1)
        {
            GD.Print($"[Oiapi] 搜索失败，code={GetRootCode(doc)}, message={GetRootMessage(doc)}");
            return null;
        }

        if (!doc.RootElement.TryGetProperty("data", out var dataElem) || dataElem.ValueKind != JsonValueKind.Array)
        {
            GD.Print("[Oiapi] 搜索结果 data 不是数组");
            return null;
        }

        var songs = new List<SongInfo>();
        foreach (var item in dataElem.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElem) || !item.TryGetProperty("name", out var nameElem))
                continue;

            string id = idElem.GetInt64().ToString();
            string name = nameElem.GetString();
            string artist = "";
            if (item.TryGetProperty("singer", out var singerElem) && singerElem.ValueKind == JsonValueKind.Array && singerElem.GetArrayLength() > 0)
            {
                artist = singerElem[0].GetString();
            }

            songs.Add(new SongInfo { Id = id, Name = name, Artist = artist });
        }

        GD.Print($"[Oiapi] 搜索成功，找到 {songs.Count} 首候选");
        return songs;
    }

    public override async Task<string> GetLyricAsync(SongInfo song)
    {
        // 默认请求 LRC 格式
        string url = $"{ApiUrl}?id={song.Id}&format=lrc";
        GD.Print($"[Oiapi] 获取歌词请求: {url}");
        using var doc = await GetJsonAsync(url);
        if (doc == null) return null;

        if (GetRootCode(doc) != 1)
        {
            GD.Print($"[Oiapi] 获取歌词失败，code={GetRootCode(doc)}, message={GetRootMessage(doc)}");
            return null;
        }

        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("content", out var content))
        {
            GD.Print("[Oiapi] 歌词数据缺少 content 字段");
            return null;
        }

        string lyric = content.GetString();
        if (string.IsNullOrEmpty(lyric))
        {
            GD.Print("[Oiapi] 歌词内容为空");
            return null;
        }

        GD.Print($"[Oiapi] 歌词获取成功，长度: {lyric.Length} 字符");
        return lyric;
    }
}
