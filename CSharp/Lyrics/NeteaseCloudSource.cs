using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HttpClient = System.Net.Http.HttpClient;

/// <summary>
/// 网易云音乐歌词源（通过 vkeys.cn V2 API）
/// 搜索：/v2/music/netease?word=xxx
/// 歌词：/v2/music/netease/lyric?id=xxx
/// </summary>
public class NeteaseCloudSource : LyricsHttpSourceBase
{
    private const string SearchUrl = "https://api.vkeys.cn/v2/music/netease";
    private const string LyricUrl = "https://api.vkeys.cn/v2/music/netease/lyric";

    public NeteaseCloudSource(HttpClient http) : base(http) { }

    protected override string SourceName => "NeteaseCloud";

    public override async Task<List<SongInfo>> SearchAsync(string keyword)
    {
        string url = $"{SearchUrl}?word={Uri.EscapeDataString(keyword)}";
        GD.Print($"[NeteaseCloud] 搜索请求: {url}");
        using var doc = await GetJsonAsync(url);
        if (doc == null) return null;

        if (GetRootCode(doc) != 200)
        {
            GD.Print($"[NeteaseCloud] 搜索失败，code={GetRootCode(doc)}, message={GetRootMessage(doc)}");
            return null;
        }

        var songs = new List<SongInfo>();
        if (!doc.RootElement.TryGetProperty("data", out var data))
        {
            GD.Print("[NeteaseCloud] 搜索结果缺少 data 字段");
            return null;
        }

        // 兼容两种返回：单曲对象 或 歌曲数组
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (TryParseSongItem(item, out var song))
                    songs.Add(song);
            }
        }
        else if (data.ValueKind == JsonValueKind.Object)
        {
            if (TryParseSongItem(data, out var song))
                songs.Add(song);
        }
        else
        {
            GD.Print("[NeteaseCloud] 搜索返回 data 类型未知");
            return null;
        }

        GD.Print($"[NeteaseCloud] 搜索成功，找到 {songs.Count} 首候选");
        return songs;
    }

    public override async Task<string> GetLyricAsync(SongInfo song)
    {
        string url = $"{LyricUrl}?id={song.Id}";
        GD.Print($"[NeteaseCloud] 获取歌词请求: {url}");
        using var doc = await GetJsonAsync(url);
        if (doc == null) return null;

        if (GetRootCode(doc) != 200)
        {
            GD.Print($"[NeteaseCloud] 获取歌词失败，code={GetRootCode(doc)}, message={GetRootMessage(doc)}");
            return null;
        }

        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("lrc", out var lrcElem))
        {
            GD.Print("[NeteaseCloud] 歌词数据缺少 data.lrc 字段");
            return null;
        }

        string lyric = lrcElem.GetString();
        if (string.IsNullOrEmpty(lyric))
        {
            GD.Print("[NeteaseCloud] 歌词内容为空");
            return null;
        }

        GD.Print($"[NeteaseCloud] 歌词获取成功，长度: {lyric.Length} 字符");
        return lyric;
    }

    private static bool TryParseSongItem(JsonElement item, out SongInfo song)
    {
        song = null;
        if (!item.TryGetProperty("id", out var idElem) || !item.TryGetProperty("song", out var nameElem))
            return false;

        string id = idElem.GetInt64().ToString();
        string name = nameElem.GetString();
        string artist = "";
        if (item.TryGetProperty("singer", out var singerElem))
            artist = singerElem.GetString();

        song = new SongInfo { Id = id, Name = name, Artist = artist };
        return true;
    }
}
