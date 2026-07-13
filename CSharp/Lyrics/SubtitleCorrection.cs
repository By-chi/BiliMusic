using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;
using HttpClient = System.Net.Http.HttpClient;

public partial class SubtitleCorrection : Node
{
    [Signal]
    public delegate void SubtitleProcessedEventHandler(string lrcPath, string requestId);

    private static readonly HttpClient httpClient = CreateHttpClient();
    private static LyricsFetcher _lyricsFetcher;

    private const double MinTextScoreForMatch = 0.35;

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("User-Agent", "BiliMusicPlayer/1.0");
        return client;
    }

    public override void _Ready()
    {
        base._Ready();
        SubtitleUtils.LoadTSDictionary();
        _lyricsFetcher = new LyricsFetcher(httpClient,
            new OiapiSource(httpClient),
            new NeteaseCloudSource(httpClient),
            new LrclibSource(httpClient)
        );
    }

    public async void ProcessSubtitleAsync(Godot.Collections.Dictionary subtitleContent, string m4sPath, string trackName, string outputDir, string requestId)
    {
        GD.Print($"[SubtitleCorrection] 开始, 曲目: {trackName}, 请求ID: {requestId}");
        outputDir = ProjectSettings.GlobalizePath(outputDir);
        Directory.CreateDirectory(outputDir);
        string result = await ProcessSubtitleInternal(subtitleContent, m4sPath, trackName, outputDir);
        EmitSignal(SignalName.SubtitleProcessed, result ?? "", requestId);
    }

    public async void FetchAndAlignExternalAsync(string m4sPath, string trackName, string outputDir, string requestId)
    {
        GD.Print($"[SubtitleCorrection] 外部歌词开始, 曲目: {trackName}, 请求ID: {requestId}");
        outputDir = ProjectSettings.GlobalizePath(outputDir);
        Directory.CreateDirectory(outputDir);
        string result = await FetchAndAlignExternalInternal(m4sPath, trackName, outputDir);
        EmitSignal(SignalName.SubtitleProcessed, result ?? "", requestId);
    }

    private async Task<string> ProcessSubtitleInternal(
        Godot.Collections.Dictionary subtitleContent,
        string m4sPath, string trackName, string outputDir, string requestId = null)
    {
        var biliSubs = ParseBiliSubs(subtitleContent);
        GD.Print($"[Process] 解析到 B 站字幕 {biliSubs.Count} 条");

        if (biliSubs.Count == 0)
        {
            GD.Print("[Process] 传入字幕无效，切换为纯外部获取模式");
            return await FetchAndAlignExternalInternal(m4sPath, trackName, outputDir);
        }

        float audioDuration = 0f;
        try { audioDuration = (float)GetNode("/root/Player").Call("get_duration"); }
        catch { audioDuration = 300f; }

        var biliTexts = biliSubs.Select(s => s.content).ToList();
        string rawLrc = await _lyricsFetcher.FetchLyricsAsync(trackName, biliTexts, audioDuration);
        string finalLrcPath = Path.Combine(outputDir, $"{Path.GetFileNameWithoutExtension(m4sPath)}.lrc");

        if (!string.IsNullOrEmpty(rawLrc))
        {
            string cleaned = SubtitleUtils.CleanLrcMeta(rawLrc);
            List<string> externalLines = SubtitleUtils.ExtractLyricLinesFromLrc(cleaned);

            // 过滤掉包含 '-' 的行（如某些时间标签分隔符）
            externalLines = [.. externalLines.Where(line => !line.Contains('-'))];
            GD.Print($"[Process] 过滤后外部歌词行数: {externalLines.Count}");

            if (externalLines.Count > 1 && !IsPlaceholderLyric(externalLines))
            {
                var biliEntries = biliSubs.Select(s => (time: s.from, text: s.content)).ToList();

                // ---- 调试日志：外部歌词 ----
                GD.Print("[DEBUG] ========== 过滤后外部歌词 ==========");
                for (int i = 0; i < externalLines.Count; i++)
                    GD.Print($"[DEBUG Ext {i:D3}] {externalLines[i]}");

                // ---- 调试日志：B站字幕（用于对齐） ----
                GD.Print("[DEBUG] ========== 过滤后B站字幕（用于对齐） ==========");
                var biliDebug = biliEntries
                    .Select(x => (x.time, norm: SubtitleUtils.NormalizeText(x.text), raw: x.text))
                    .Where(x => !string.IsNullOrWhiteSpace(x.norm) && x.norm.Length >= 2
                                && !Regex.IsMatch(x.norm, @"^[\d\.\,\;\:\!?\-\+\(\)\[\]\{\}\s]+$"))
                    .ToList();
                for (int i = 0; i < biliDebug.Count; i++)
                    GD.Print($"[DEBUG Bili {i:D3}] [{biliDebug[i].time:F2}s] {biliDebug[i].raw}  (norm: {biliDebug[i].norm})");

                // 调用对齐算法
                var aligned = SubtitleUtils.AlignBilibiliToExternalMulti(
                    externalLines: externalLines,
                    bilibiliEntries: biliEntries,
                    similarityThreshold: MinTextScoreForMatch
                );

                if (aligned.Count == externalLines.Count && aligned.Count > 0)
                {
                    var sb = new StringBuilder();
                    foreach (var (t, txt) in aligned)
                    {
                        var ts = TimeSpan.FromSeconds(t);
                        sb.AppendLine($"[{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D2}]{txt}");
                    }
                    await File.WriteAllTextAsync(finalLrcPath, sb.ToString(), Encoding.UTF8);
                    GD.Print($"[Process] 新对齐成功，输出 {aligned.Count} 行歌词");
                    return finalLrcPath;
                }
                else
                {
                    GD.Print($"[Process] 对齐行数不匹配（外部{externalLines.Count}行，对齐结果{aligned.Count}行），回退 B 站字幕");
                }
            }
            else if (externalLines.Count <= 1)
            {
                GD.Print("[Process] 外部歌词行数过少，无法对齐，回退 B 站字幕");
            }
            else
            {
                GD.Print("[Process] 外部歌词为占位文本（暂无歌词/纯音乐等），回退 B 站字幕");
            }
        }

        GD.Print("[Process] 回退输出 B 站字幕");
        string biliLrc = ConvertBiliSubsToLrc(biliSubs, true);
        await File.WriteAllTextAsync(finalLrcPath, biliLrc, Encoding.UTF8);
        return finalLrcPath;
    }

    // 辅助方法：判断外部歌词是否为无效占位文本
    private static bool IsPlaceholderLyric(List<string> lines)
    {
        string allText = string.Join("", lines);
        return allText.Contains("暂无歌词") ||
            allText.Contains("纯音乐") ||
            allText.Contains("请欣赏") ||
            allText.Contains("暂时无法获取歌词") ||
            allText.Contains("此歌曲为没有填词的纯音乐");
    }
        
    private async Task<string> FetchAndAlignExternalInternal(string m4sPath, string trackName, string outputDir)
    {
        GD.Print($"[External] 开始获取外部歌词，曲目: {trackName}");
        float audioDuration = (float)GetNode("/root/Player").Call("get_duration");
        string rawLrc = await _lyricsFetcher.FetchLyricsAsync(trackName, null, audioDuration);
        if (string.IsNullOrEmpty(rawLrc))
        {
            GD.PrintErr("[External] 所有歌词源均无结果");
            return null;
        }
        string cleaned = SubtitleUtils.CleanLrcMeta(rawLrc);
        if (string.IsNullOrWhiteSpace(cleaned) || !SubtitleUtils.ContainsTimestamps(cleaned))
        {
            GD.PrintErr("[External] 歌词内容无效（无时间轴）");
            return null;
        }
        string finalLrcPath = Path.Combine(outputDir, $"{Path.GetFileNameWithoutExtension(m4sPath)}.lrc");
        await File.WriteAllTextAsync(finalLrcPath, cleaned, Encoding.UTF8);
        return finalLrcPath;
    }

    private static string ConvertBiliSubsToLrc(List<BiliSubtitleItem> subs, bool toSimplified = true)
    {
        var sb = new StringBuilder();
        foreach (var sub in subs)
        {
            string text = toSimplified ? SubtitleUtils.ToSimplified(sub.content) : sub.content;
            var ts = TimeSpan.FromSeconds(sub.from);
            sb.AppendLine($"[{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D2}]{text}");
        }
        return sb.ToString();
    }

    private static List<BiliSubtitleItem> ParseBiliSubs(Godot.Collections.Dictionary subtitleContent)
    {
        var list = new List<BiliSubtitleItem>();
        if (!subtitleContent.ContainsKey("body")) return list;
        foreach (var entry in subtitleContent["body"].AsGodotArray())
        {
            var dict = entry.AsGodotDictionary();
            list.Add(new BiliSubtitleItem
            {
                from = (double)dict["from"],
                to = (double)dict["to"],
                content = ((string)dict["content"]).Replace("♪", "").Trim()
            });
        }
        return list;
    }

    private class BiliSubtitleItem { public double from; public double to; public string content; }
}