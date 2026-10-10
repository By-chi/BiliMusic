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

/// <summary>
/// 歌词管线，优先级：
///   1. B站非AI字幕 → 直接转换输出（字幕即真相）
///   2. B站AI字幕 + 外部歌词 → DP对齐：时间戳用AI字幕，文本用外部
///   3. 仅外部歌词 → 打分选最佳，用外部原始时间轴
///
/// 歌手先验：videoTitle 经 singer.txt 词库扫描提取歌手名，
/// 用于增强搜索关键词与选歌打分；标题为空/提取失败时优雅回退旧行为。
/// </summary>
public partial class LyricsPipeline : Node
{
    [Signal] public delegate void SubtitleProcessedEventHandler(string lrcPath, string requestId);

    private const int    MaxDownloadCandidates = 4;     // download-then-verify 下载额度
    private const double TailThresholdSec       = 15;   // 歌词末行距歌曲结尾的预期余量
    private const double MinAlignQuality        = 0.22; // 对齐质量低于此 → 回退AI字幕原文
    private const double MinMatchThreshold      = 0.32; // 单行锚定可信分下限

    private static readonly HttpClient _http = CreateHttpClient();

    private OiapiSource _oiapiSource;
    private NeteaseCloudSource _neteaseSource;
    private LrclibSource _lrclibSource;
    private List<ILyricsSource> _sources;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _inFlight = new();

    public override void _Ready()
    {
        base._Ready();
        SubtitleUtils.LoadTSDictionary();
        _oiapiSource   = new OiapiSource(_http);
        _neteaseSource = new NeteaseCloudSource(_http);
        _lrclibSource  = new LrclibSource(_http);
        _sources = [_oiapiSource, _neteaseSource, _lrclibSource];
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("User-Agent", "BiliMusicPlayer/1.0");
        return client;
    }


    /// <summary>
    /// 入口1：有B站字幕内容。isAiSubtitle=false 时字幕即真相直接输出；
    /// =true 时走外部歌词+DP对齐。
    /// subtitleContent：B站字幕JSON（GDScript传Dictionary或JSON字符串均可）。
    /// videoTitle：视频标题（可 null），用于歌手先验与搜索增强。
    /// requestId：调用方生成的请求ID，用于回调关联（handle_correction_result 靠它查 _pending）。
    /// </summary>
    public async void ProcessSubtitleAsync(
        Variant subtitleContent,
        string m4sPath,
        string trackName,
        string outputDir,
        bool isAiSubtitle,
        string videoTitle = null,
        string requestId = null)
    {
        if (string.IsNullOrEmpty(requestId))
            requestId = $"{Time.GetTicksMsec() % 1000000}_{GD.Randi() % 1000000000}";

        // ── 并发去重：同一音频文件已有任务在跑则直接忽略 ──
        string flightKey = $"sub_{Path.GetFileNameWithoutExtension(m4sPath)}";
        if (!_inFlight.TryAdd(flightKey, 0))
        {
            GD.Print($"[Pipeline] 忽略重复请求: {trackName} (同曲目任务进行中)");
            return;
        }

        try
        {
            GD.Print($"[Pipeline] 开始, 曲目: {trackName}, AI字幕: {isAiSubtitle}, 请求ID: {requestId}");
            GD.Print($"[Pipeline] videoTitle: {videoTitle ?? "(null)"}");

            outputDir = ProjectSettings.GlobalizePath(outputDir);
            Directory.CreateDirectory(outputDir);

            string result;

            // ---- 优先级1：非AI字幕 = 真相，直接转换 ----
            if (!isAiSubtitle)
            {
                var subs = ParseBiliSubs(ToDict(subtitleContent));
                if (subs.Count > 0)
                {
                    string lrcPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(m4sPath) + ".lrc");
                    await File.WriteAllTextAsync(lrcPath, ConvertToLrc(subs), Encoding.UTF8);
                    GD.Print($"[Pipeline] ✓ B站人工字幕直接输出: {subs.Count} 行");
                    result = lrcPath;
                }
                else
                {
                    GD.PrintErr("[Pipeline] 人工字幕解析为空，回退外部歌词");
                    result = await FetchExternalAsync(m4sPath, trackName, outputDir, videoTitle);
                }
            }
            else
            {
                // ---- 优先级2/3 ----
                result = await ProcessInternal(subtitleContent, m4sPath, trackName, outputDir, videoTitle);
            }

            EmitSignal(SignalName.SubtitleProcessed, result ?? "", requestId);
        }
        catch (Exception e)
        {
            GD.PrintErr($"[Pipeline] 异常: {e}");
            EmitSignal(SignalName.SubtitleProcessed, "", requestId);
        }
        finally
        {
            _inFlight.TryRemove(flightKey, out _);
        }
    }

    /// <summary>
    /// 入口2：无B站字幕，纯外部获取。
    /// videoTitle：视频标题（可 null），用于歌手先验与搜索增强。
    /// </summary>
    public async void FetchAndAlignExternalAsync(
        string m4sPath, string trackName, string outputDir, string requestId,
        string videoTitle = null)
    {
        if (string.IsNullOrEmpty(requestId))
            requestId = $"{Time.GetTicksMsec() % 1000000}_{GD.Randi() % 1000000000}";

        string flightKey = $"ext_{Path.GetFileNameWithoutExtension(m4sPath)}";
        if (!_inFlight.TryAdd(flightKey, 0))
        {
            GD.Print($"[Pipeline] 忽略重复请求: {trackName} (同曲目任务进行中)");
            return;
        }

        try
        {
            GD.Print($"[Pipeline] 外部歌词模式, 曲目: {trackName}, 请求ID: {requestId}");
            GD.Print($"[Pipeline] videoTitle: {videoTitle ?? "(null)"}");

            outputDir = ProjectSettings.GlobalizePath(outputDir);
            Directory.CreateDirectory(outputDir);

            string result = await FetchExternalAsync(m4sPath, trackName, outputDir, videoTitle);
            EmitSignal(SignalName.SubtitleProcessed, result ?? "", requestId);
        }
        catch (Exception e)
        {
            GD.PrintErr($"[Pipeline] 异常: {e}");
            EmitSignal(SignalName.SubtitleProcessed, "", requestId);
        }
        finally
        {
            _inFlight.TryRemove(flightKey, out _);
        }
    }


    /// <summary>AI字幕 + 外部歌词 → DP对齐；失败/质量不足 → 回退纯外部</summary>
    private async Task<string> ProcessInternal(
        Variant subtitleContent,
        string m4sPath,
        string trackName,
        string outputDir,
        string videoTitle)
    {
        var aiSubs = ParseBiliSubs(ToDict(subtitleContent));
        string aiFullText = aiSubs.Count > 0 ? string.Join("\n", aiSubs.Select(s => s.content)) : null;
        bool hasAi = !string.IsNullOrEmpty(aiFullText);
        if (hasAi)
            GD.Print($"[Pipeline] B站AI字幕 {aiSubs.Count} 条 (AI: True)");
        else
            GD.PrintErr("[Pipeline] AI字幕标记为true但解析为空，按纯外部处理");

        double audioDuration = GetAudioDuration();

        var best = await SelectBestLyricAsync(trackName, aiFullText, audioDuration, videoTitle);
        if (best == null)
        {
            // 无外部歌词可用：有AI字幕则直接输出AI原文（聊胜于无）
            if (hasAi)
            {
                GD.Print("[Pipeline] 外部歌词不可用，输出AI字幕原文");
                string aiOnlyPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(m4sPath) + ".lrc");
                await File.WriteAllTextAsync(aiOnlyPath, ConvertToLrc(aiSubs), Encoding.UTF8);
                return aiOnlyPath;
            }
            GD.PrintErr("[Pipeline] ✗ 所有源均无有效歌词");
            return null;
        }

        if (hasAi)
        {
            try
            {
                var aligner = new LyricAligner(TailThresholdSec);
                var aiTuples = aiSubs.Select(s => (s.from, s.content)).ToList();
                var alignResult = await aligner.AlignAsync(aiTuples, best);

                if (alignResult.Quality >= MinAlignQuality)
                {
                    GD.Print($"[Pipeline] ✓ 对齐成功: {alignResult.AnchoredCount}/{alignResult.TotalLines} 行锚定, " +
                            $"质量 {alignResult.Quality:F3}");
                    string lrcPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(m4sPath) + ".lrc");
                    await File.WriteAllTextAsync(lrcPath, alignResult.ToLrc(), Encoding.UTF8);
                    return lrcPath;
                }
                GD.Print($"[Pipeline] 对齐质量不足({alignResult.Quality:F3} < {MinAlignQuality})，回退纯外部歌词");
            }
            catch (Exception e)
            {
                GD.PrintErr($"[Pipeline] 对齐失败，回退纯外部歌词: {e.Message}");
            }
        }

        // 守门：有AI字幕但最佳候选文本分极低 → 搜索/选歌几乎肯定错了，
        // 输出错误歌曲的歌词（如 RUDE!）还不如输出AI字幕原文
        if (hasAi && best.TextScore < 0.05)
        {
            GD.Print($"[Pipeline] 最佳候选文本分过低({best.TextScore:F3})，疑似选错歌曲，输出AI字幕原文");
            string aiOnlyPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(m4sPath) + ".lrc");
            await File.WriteAllTextAsync(aiOnlyPath, ConvertToLrc(aiSubs), Encoding.UTF8);
            return aiOnlyPath;
        }

        return await WriteExternalLrcAsync(best, m4sPath, outputDir);
    }

    /// <summary>纯外部获取（入口2的共用实现）</summary>
    private async Task<string> FetchExternalAsync(
        string m4sPath, string trackName, string outputDir, string videoTitle)
    {
        double duration = GetAudioDuration();
        var best = await SelectBestLyricAsync(trackName, null, duration, videoTitle);
        if (best == null)
        {
            GD.PrintErr("[Pipeline] ✗ 所有源均无有效歌词");
            return null;
        }
        return await WriteExternalLrcAsync(best, m4sPath, outputDir);
    }

    private async Task<string> WriteExternalLrcAsync(ScoredLyric best, string m4sPath, string outputDir)
    {
        // 保留外部歌词原始时间戳——它们比我们自己重建的准
        string lrcPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(m4sPath) + ".lrc");
        await File.WriteAllTextAsync(lrcPath, best.CleanedLrc + System.Environment.NewLine, Encoding.UTF8);
        GD.Print($"[Pipeline] ✓ 纯外部歌词: {best.Song.Name} - {best.Song.Artist} (来自 {best.Source.GetType().Name})");
        return lrcPath;
    }


    private async Task<ScoredLyric> SelectBestLyricAsync(
        string trackName, string aiFullText, double duration, string videoTitle)
    {
        string artistHint = SongInfoExtractor.ExtractSingerHint(videoTitle ?? "");
        if (!string.IsNullOrEmpty(artistHint))
            GD.Print($"[Pipeline] 歌手提示: {artistHint}（来自视频标题）");

        string searchKeyword = string.IsNullOrEmpty(artistHint)
            ? trackName
            : $"{trackName} {artistHint}";

        // Oiapi 的 URL 内部已硬编码 limit=10
        List<SongInfo> oiapiResults = null, neteaseResults = null, lrclibResults = null;
        try { oiapiResults = await _oiapiSource.SearchAsync(searchKeyword); }
        catch (Exception e) { GD.PrintErr($"[Oiapi] 搜索失败: {e.Message}"); }
        try { neteaseResults = await _neteaseSource.SearchAsync(searchKeyword); }
        catch (Exception e) { GD.PrintErr($"[NeteaseCloud] 搜索失败: {e.Message}"); }
        try { lrclibResults = await _lrclibSource.SearchAsync(searchKeyword); }
        catch (Exception e) { GD.PrintErr($"[LRCLIB] 搜索失败: {e.Message}"); }

        var candidates = DedupeCandidates(oiapiResults, neteaseResults, lrclibResults);
        GD.Print($"[Pipeline] 搜索到 {candidates.Count} 个去重候选");
        if (candidates.Count == 0) return null;

        var scored = await LyricCandidateSelector.ScoreCandidatesAsync(
            candidates, MaxDownloadCandidates, duration > 0 ? duration : (double?)null,
            TailThresholdSec, aiFullText, trackName, videoTitle);

        return scored.FirstOrDefault();
    }

    /// <summary>按 (歌名, 歌手) 归一化去重，跨源合并</summary>
    private List<(ILyricsSource source, SongInfo song)> DedupeCandidates(
        List<SongInfo> oiapiResults,
        List<SongInfo> neteaseResults,
        List<SongInfo> lrclibResults)
    {
        var result = new List<(ILyricsSource, SongInfo)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(IEnumerable<SongInfo> list, ILyricsSource src)
        {
            if (list == null) return;
            foreach (var s in list)
            {
                if (s == null || string.IsNullOrWhiteSpace(s.Name)) continue;
                string key = $"{TextSimilarity.Normalize(s.Name)}|{TextSimilarity.Normalize(s.Artist ?? "")}";
                if (!seen.Add(key)) continue;
                result.Add((src, s));
            }
        }

        Add(oiapiResults, _oiapiSource);
        Add(neteaseResults, _neteaseSource);
        Add(lrclibResults, _lrclibSource);
        return result;
    }


    private double GetAudioDuration()
    {
        try { return (float)GetNode("/root/Player").Call("get_duration"); }
        catch { return 0; }
    }

    /// <summary>兼容 Dictionary / JSON字符串 两种形态，统一转为 Godot Dictionary</summary>
    private static Godot.Collections.Dictionary ToDict(Variant subtitleContent)
    {
        // 形态1：调用方直接传 Dictionary（GDScript 场景）
        if (subtitleContent.VariantType == Variant.Type.Dictionary)
            return subtitleContent.AsGodotDictionary();
        if (subtitleContent.VariantType == Variant.Type.String)
        {
            string s = subtitleContent.AsString();
            if (string.IsNullOrEmpty(s)) return null;
            try
            {
                var parsed = Json.ParseString(s);
                if (parsed.VariantType == Variant.Type.Dictionary)
                    return parsed.AsGodotDictionary();
                if (parsed.VariantType == Variant.Type.Array)   // 裸数组包一层 body
                {
                    var wrapper = new Godot.Collections.Dictionary();
                    wrapper["body"] = parsed.AsGodotArray();
                    return wrapper;
                }
            }
            catch { }
        }
        return null;
    }

    private static List<BiliSub> ParseBiliSubs(Godot.Collections.Dictionary subtitleContent)
    {
        var list = new List<BiliSub>();
        if (subtitleContent == null || !subtitleContent.ContainsKey("body")) return list;
        foreach (var entry in subtitleContent["body"].AsGodotArray())
        {
            var d = entry.AsGodotDictionary();
            if (!d.ContainsKey("from") || !d.ContainsKey("content")) continue;
            string content = d["content"].AsString().Replace("♪", "").Trim();
            content = Regex.Replace(content, "【[^】]*】", "").Trim(); // 去掉【音乐】【掌声】等标记
            if (content.Length == 0) continue;
            list.Add(new BiliSub
            {
                from = d["from"].AsDouble(),
                to = d.ContainsKey("to") ? d["to"].AsDouble() : d["from"].AsDouble() + 2,
                content = content
            });
        }
        list.Sort((a, b) => a.from.CompareTo(b.from));
        return list;
    }

    private class BiliSub { public double from; public double to; public string content; }

    private static string ConvertToLrc(List<BiliSub> subs)
    {
        var sb = new StringBuilder();
        foreach (var s in subs) sb.AppendLine($"{Fmt(s.from)}{SubtitleUtils.ToSimplified(s.content)}");
        return sb.ToString();
    }

    private static string Fmt(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"[{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D2}]";
    }
}
