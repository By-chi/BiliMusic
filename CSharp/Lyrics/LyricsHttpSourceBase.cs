using System;
using System.Collections.Generic;
using HttpClient = System.Net.Http.HttpClient;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;

/// <summary>
/// 歌词源公共基类：统一 HttpClient 生命周期与公共请求/解析逻辑。
/// 三个具体歌词源（Lrclib/NeteaseCloud/Oiapi）只保留各自的 URL 与字段解析。
/// </summary>
public abstract class LyricsHttpSourceBase : ILyricsSource
{
    protected readonly HttpClient _http;

    protected LyricsHttpSourceBase(HttpClient http)
    {
        _http = http;
    }

    public abstract Task<List<SongInfo>> SearchAsync(string keyword);
    public abstract Task<string> GetLyricAsync(SongInfo song);

    /// <summary>发起 GET 请求并返回 JSON 文档（失败返回 null）</summary>
    protected async Task<JsonDocument> GetJsonAsync(string url)
    {
        try
        {
            string json = await _http.GetStringAsync(url);
            return JsonDocument.Parse(json);
        }
        catch (Exception e)
        {
            GD.PrintErr($"[{SourceName}] 请求异常: {e.GetType().Name} - {e.Message}");
            return null;
        }
    }

    /// <summary>读取根对象上的 int 状态码（不存在返回 -1）</summary>
    protected static int GetRootCode(JsonDocument doc)
    {
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object)
            return -1;
        return doc.RootElement.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.Number
            ? code.GetInt32()
            : -1;
    }

    /// <summary>读取根对象上的 message 字段</summary>
    protected static string GetRootMessage(JsonDocument doc, string fallback = "无消息")
    {
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object)
            return fallback;
        return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? fallback : fallback;
    }

    /// <summary>歌词源显示名（用于日志前缀），默认取类名，可覆写</summary>
    protected virtual string SourceName => GetType().Name;
}
