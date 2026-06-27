using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;
using NDtw;
using NPinyin;
using HttpClient = System.Net.Http.HttpClient;
public partial class SubtitleCorrection : Node
{
    [Signal]
    public delegate void SubtitleProcessedEventHandler(string lrcPath, string requestId);

    private static readonly HttpClient httpClient = CreateHttpClient();
    private static LyricsFetcher _lyricsFetcher;

    private const double SmallOffsetThreshold = 0.7;
    private const double MinTextScoreForMatch = 0.6;

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
            new WwoyunSource(httpClient),
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
            var externalSubs = ParseLrcToSubtitleItems(cleaned);
            
            // 转为 SubtitleItem 列表（用于评估和对齐映射）
            var biliItems = biliSubs.Select(s => new LyricsAlignment.SubtitleItem
            {
                StartTime = TimeSpan.FromSeconds(s.from),
                EndTime = TimeSpan.FromSeconds(s.to),
                Text = s.content
            }).ToList();

            var aligner = new LyricsAlignment.LyricsAligner();
            var alignmentResult = aligner.Evaluate(biliItems, externalSubs);
            GD.Print($"[对齐评估] TextScore={alignmentResult.TextScore:F4}, TimeScore={alignmentResult.TimeScore:F4}, WeightedScore={alignmentResult.WeightedOverallScore:F4}, Offset={alignmentResult.EstimatedOffsetSeconds:F2}s, IsMatch={alignmentResult.IsMatch}");

            if (alignmentResult.TextScore >= MinTextScoreForMatch)
            {
                try
                {
                    // 获取句子级对齐映射（B 站行索引 → 外部行索引）
                    var mapping = aligner.GetSentenceAlignment(biliItems, externalSubs);

                    var sb = new StringBuilder();
                    var extTexts = externalSubs.Select(e => SubtitleUtils.ToSimplified(e.Text)).ToList();
                    var biliSimpTexts = biliSubs.Select(s => SubtitleUtils.ToSimplified(s.content)).ToList();

                    // 每个 B 站行只保留第一个匹配到的外部行
                    var biliToExt = new Dictionary<int, int>();
                    foreach (var (bIdx, eIdx) in mapping)
                    {
                        if (!biliToExt.ContainsKey(bIdx))
                            biliToExt[bIdx] = eIdx;
                    }

                    for (int i = 0; i < biliSubs.Count; i++)
                    {
                        string text;
                        if (biliToExt.TryGetValue(i, out int extIdx) && extIdx < extTexts.Count)
                        {
                            text = extTexts[extIdx];   // 使用对应的外部歌词文本
                        }
                        else
                        {
                            text = biliSimpTexts[i];   // 无匹配时回退到 B 站文本
                        }

                        var ts = TimeSpan.FromSeconds(biliSubs[i].from);
                        sb.AppendLine($"[{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D2}]{text}");
                    }

                    await File.WriteAllTextAsync(finalLrcPath, sb.ToString(), Encoding.UTF8);
                    GD.Print($"[Process] 已基于文本对齐使用 B 站时间轴 + 外部歌词文本");
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[Process] 对齐替换失败: {ex}");
                    // 异常时回退到 B 站字幕
                    string fallbackLrc = ConvertBiliSubsToLrc(biliSubs, true);
                    await File.WriteAllTextAsync(finalLrcPath, fallbackLrc, Encoding.UTF8);
                }
                return finalLrcPath;
            }
            else
            {
                GD.Print($"[Process] 文本相似度 {alignmentResult.TextScore:F4} < {MinTextScoreForMatch}，回退到 B 站字幕");
            }
        }

        GD.Print("[Process] 进入回退分支，输出 B 站字幕（简体）");
        string biliLrc = ConvertBiliSubsToLrc(biliSubs, true);
        await File.WriteAllTextAsync(finalLrcPath, biliLrc, Encoding.UTF8);
        return finalLrcPath;
    }
    /// <summary>
    /// 使用外部歌词的文本内容，替换B站字幕的时间轴中的文本，尽量完整保留外部文本。
    /// </summary>
    private static string ReplaceTimeTexts(string cleanedLrc, List<BiliSubtitleItem> biliSubs)
    {
        // 1提取外部歌词的文本（按行，保留空格，仅Trim）
        var extTexts = new List<string>();
        foreach (Match m in Regex.Matches(cleanedLrc, 
                @"^\[(\d{2}):(\d{2})\.(\d{2,3})\](.*)", RegexOptions.Multiline))
        {
            string text = m.Groups[4].Value.Trim();
            if (!string.IsNullOrEmpty(text))
                extTexts.Add(text);
        }

        if (extTexts.Count == 0 || biliSubs.Count == 0)
            return ConvertBiliSubsToLrc(biliSubs, true); // 回退

        //  对所有文本进行繁简转换
        var simpBiliTexts = biliSubs.Select(s => SubtitleUtils.ToSimplified(s.content).Trim()).ToList();
        var simpExtTexts = extTexts.Select(SubtitleUtils.ToSimplified).ToList();

        //  构建输出：B站时间戳 + 外部文本（按顺序填充）
        var sb = new StringBuilder();
        int extIndex = 0;
        for (int i = 0; i < biliSubs.Count; i++)
        {
            string textToUse;
            if (extIndex < simpExtTexts.Count)
            {
                textToUse = simpExtTexts[extIndex];
                extIndex++;
            }
            else
            {
                // 外部歌词行不够，使用原B站字幕文本（或留空）
                textToUse = simpBiliTexts[i];
            }

            var ts = TimeSpan.FromSeconds(biliSubs[i].from);
            sb.AppendLine($"[{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D2}]{textToUse}");
        }

        //  如果外部歌词行数多于B站字幕，丢弃剩余外部行（或可选择追加到最后一个时间戳，但会破坏对齐）
        return sb.ToString();
    }
    private static string MergeWithBiliSubs(string cleanedLrc, List<BiliSubtitleItem> biliSubs, double offsetSeconds)
    {
        // 1. 提取外部歌词文本（保留原始空格，仅 Trim）
        var extRawLines = new List<string>();
        foreach (Match m in Regex.Matches(cleanedLrc, @"^\[(\d{2}):(\d{2})\.(\d{2,3})\](.*)", RegexOptions.Multiline))
        {
            string text = m.Groups[4].Value.Trim();
            if (!string.IsNullOrEmpty(text))
                extRawLines.Add(text);
        }
        if (extRawLines.Count == 0)
            return ConvertBiliSubsToLrc(biliSubs, true);

        // 保留空格：外部歌词句内空格不删除，句间用空格连接
        string extJoined = string.Join(" ", extRawLines.Select(l => SubtitleUtils.ToSimplified(l).Trim()));
        if (string.IsNullOrEmpty(extJoined))
            return ConvertBiliSubsToLrc(biliSubs, true);

        // 2. 准备 B 站字幕规范化文本（同样保留空格）
        var biliNormalized = new List<string>(biliSubs.Count);
        var biliCharToLine = new List<int>();
        for (int i = 0; i < biliSubs.Count; i++)
        {
            string norm = SubtitleUtils.ToSimplified(biliSubs[i].content).Trim(); // 保留空格
            biliNormalized.Add(norm);
            for (int k = 0; k < norm.Length; k++)
                biliCharToLine.Add(i);
        }
        string biliJoined = string.Join(" ", biliNormalized);  // 句间加空格
        if (string.IsNullOrEmpty(biliJoined))
            return ConvertBiliSubsToLrc(biliSubs, true);

        // 3. 字符级 DTW 对齐（距离函数：空格友好）
        var path = ComputeDtwPath(biliJoined.Length, extJoined.Length, (i, j) =>
        {
            char bc = biliJoined[i];
            char ec = extJoined[j];
            // 空格与任何字符的匹配成本为 0，否则根据是否相等
            if (bc == ' ' || ec == ' ')
                return 0;
            return bc == ec ? 0 : 1;
        });

        if (path == null || path.Count == 0)
            return ConvertBiliSubsToLrc(biliSubs, true);

        // 4. 为每个 B 站字幕行收集对应外部文本的字符范围
        var lineExtRanges = new (int start, int end)[biliSubs.Count];
        for (int i = 0; i < biliSubs.Count; i++)
            lineExtRanges[i] = (int.MaxValue, int.MinValue);

        for (int idx = 0; idx < path.Count; idx++)
        {
            int bPos = path[idx].Item1;
            int ePos = path[idx].Item2;
            int lineIdx = biliCharToLine[bPos];
            var range = lineExtRanges[lineIdx];
            lineExtRanges[lineIdx] = (
                Math.Min(range.start, ePos),
                Math.Max(range.end, ePos)
            );
        }

        // 5. 生成最终 LRC：时间戳用 B 站原始时间，文本来自外部歌词片段（缺失时保留 B 站文本）
        var sb = new StringBuilder();
        for (int i = 0; i < biliSubs.Count; i++)
        {
            string finalText;
            var range = lineExtRanges[i];
            if (range.start <= range.end && range.start >= 0 && range.end < extJoined.Length)
            {
                finalText = extJoined.Substring(range.start, range.end - range.start + 1).Trim();
                if (string.IsNullOrWhiteSpace(finalText))
                    finalText = SubtitleUtils.ToSimplified(biliSubs[i].content).Trim();
            }
            else
            {
                finalText = SubtitleUtils.ToSimplified(biliSubs[i].content).Trim();
            }

            double adjustedSeconds = biliSubs[i].from + offsetSeconds;
            if (adjustedSeconds < 0) adjustedSeconds = 0;
            var ts = TimeSpan.FromSeconds(adjustedSeconds);
            sb.AppendLine($"[{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D2}]{finalText}");
        }

        return sb.ToString();
    }

    private static List<(int, int)> ComputeDtwPath(int n, int m, Func<int, int, double> distance, int window = 0)
    {
        double[,] dtw = new double[n + 1, m + 1];
        for (int i = 0; i <= n; i++) for (int j = 0; j <= m; j++) dtw[i, j] = double.PositiveInfinity;
        dtw[0, 0] = 0;
        int w = window > 0 ? window : Math.Max(n, m);
        for (int i = 1; i <= n; i++)
        {
            int jStart = Math.Max(1, i - w), jEnd = Math.Min(m, i + w);
            for (int j = jStart; j <= jEnd; j++)
            {
                double cost = distance(i - 1, j - 1);
                double minPrev = Math.Min(dtw[i - 1, j], Math.Min(dtw[i, j - 1], dtw[i - 1, j - 1]));
                dtw[i, j] = cost + minPrev;
            }
        }
        var path = new List<(int, int)>();
        int ci = n, cj = m;
        while (ci > 0 || cj > 0)
        {
            path.Add((ci - 1, cj - 1));
            if (ci == 0) { cj--; continue; }
            if (cj == 0) { ci--; continue; }
            double min = Math.Min(dtw[ci - 1, cj], Math.Min(dtw[ci, cj - 1], dtw[ci - 1, cj - 1]));
            if (dtw[ci - 1, cj - 1] == min) { ci--; cj--; }
            else if (dtw[ci - 1, cj] == min) ci--;
            else cj--;
        }
        path.Reverse();
        return path;
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

    private static List<LyricsAlignment.SubtitleItem> ParseLrcToSubtitleItems(string lrc)
    {
        var items = new List<LyricsAlignment.SubtitleItem>();
        foreach (Match m in Regex.Matches(lrc, @"^\[(\d{2}):(\d{2})\.(\d{2,3})\](.*)", RegexOptions.Multiline))
        {
            int min = int.Parse(m.Groups[1].Value), sec = int.Parse(m.Groups[2].Value);
            int ms = int.Parse(m.Groups[3].Value.PadRight(3, '0'));
            string text = m.Groups[4].Value.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            double startSec = min * 60 + sec + ms / 1000.0;
            items.Add(new LyricsAlignment.SubtitleItem { StartTime = TimeSpan.FromSeconds(startSec), EndTime = TimeSpan.FromSeconds(startSec + 0.1), Text = text });
        }
        return items;
    }

    private static string ApplyOffsetToLrc(string lrc, double offsetSeconds)
    {
        if (Math.Abs(offsetSeconds) < 0.001) return lrc;
        var sb = new StringBuilder();
        foreach (string line in lrc.Split('\n'))
        {
            var m = Regex.Match(line, @"^\[(\d{2}):(\d{2})\.(\d{2,3})\](.*)");
            if (!m.Success) { sb.AppendLine(line); continue; }
            double time = int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value) + int.Parse(m.Groups[3].Value.PadRight(3, '0')) / 1000.0 + offsetSeconds;
            if (time < 0) time = 0;
            var ts = TimeSpan.FromSeconds(time);
            sb.AppendLine($"[{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D2}]{m.Groups[4].Value}");
        }
        return sb.ToString();
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

namespace LyricsAlignment
{
    public class SubtitleItem
    {
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public string Text { get; set; } = string.Empty;
        public double StartSeconds => StartTime.TotalSeconds;
        public double EndSeconds => EndTime.TotalSeconds;
    }

    public class AlignmentResult
    {
        public double TextScore { get; set; }
        public double TimeScore { get; set; }
        public double OverallScore => TextScore + TimeScore;
        public double WeightedOverallScore { get; set; }      // 加权分
        public double EstimatedOffsetSeconds { get; set; }
        public bool IsMatch { get; set; }
        public List<SegmentAlignmentInfo> Segments { get; set; } = new();
    }

    public class SegmentAlignmentInfo
    {
        public double BStartSec, BEndSec, EStartSec, EEndSec;
        public int BilibiliLineCount, ExternalLineCount;
        public double NormalizedCost;
        public double TextMatchRatio => 1 - NormalizedCost;
        public double SegmentOffset;
    }

    public class AlignmentOptions
    {
        public double SegmentGapThreshold { get; set; } = 5.0;
        public double SearchWindowSeconds { get; set; } = 3.0;
        public int SakoeChibaWindowSize { get; set; } = 15;
        public double TimeWeight { get; set; } = 1.0;
        public double TextWeight { get; set; } = 1.0;
        public double MaxOffsetForTimeScore { get; set; } = 3.0;
        public double MatchThreshold { get; set; } = 0.6;
        public double TextScoreWeight { get; set; } = 0.3;  // 文本占比30%
        public double TimeScoreWeight { get; set; } = 0.7;  // 时间占比70%
    }

    internal class TimedToken
    {
        public string ProcessedText;
        public double Time;
        public int LineIndex;
    }

    internal class SegmentRange
    {
        public double BStartSec, BEndSec, EStartSec, EEndSec;
        public List<SubtitleItem> BLines, ELines;
    }

    public class LyricsAligner
    {
        private readonly AlignmentOptions _options;
        public LyricsAligner(AlignmentOptions options = null) => _options = options ?? new AlignmentOptions();
        // 放在 LyricsAlignment 命名空间的 LyricsAligner 类内部
        public List<(int biliIndex, int extIndex)> GetSentenceAlignment(
            IReadOnlyList<SubtitleItem> bilibiliSubs,
            IReadOnlyList<SubtitleItem> externalSubs)
        {
            var biliTokens = TokenizeAndPhoneticize(bilibiliSubs);
            var extTokens = TokenizeAndPhoneticize(externalSubs);

            if (biliTokens.Count == 0 || extTokens.Count == 0)
                return [];

            var segments = BuildSegments(bilibiliSubs, externalSubs);
            var mapping = new List<(int biliIndex, int extIndex)>();

            foreach (var seg in segments)
            {
                var bSegTokens = biliTokens
                    .Where(t => t.Time >= seg.BStartSec && t.Time <= seg.BEndSec)
                    .ToList();
                var eSegTokens = extTokens
                    .Where(t => t.Time >= seg.EStartSec && t.Time <= seg.EEndSec)
                    .ToList();

                if (bSegTokens.Count == 0 || eSegTokens.Count == 0)
                    continue;

                var bFeatures = BuildFeatureVectors(bSegTokens);
                var eFeatures = BuildFeatureVectors(eSegTokens);
                var bSeq = bFeatures.Select(f => _options.TextWeight * f[0] + _options.TimeWeight * f[1]).ToArray();
                var eSeq = eFeatures.Select(f => _options.TextWeight * f[0] + _options.TimeWeight * f[1]).ToArray();

                var dtw = new Dtw(bSeq, eSeq);
                var path = dtw.GetPath();

                foreach (var pair in path)
                {
                    int bTokIdx = pair.Item1;
                    int eTokIdx = pair.Item2;

                    if (bTokIdx < bSegTokens.Count && eTokIdx < eSegTokens.Count)
                    {
                        int bLine = bSegTokens[bTokIdx].LineIndex;
                        int eLine = eSegTokens[eTokIdx].LineIndex;
                        mapping.Add((bLine, eLine));
                    }
                }
            }

            // 去重并按 B 站行索引排序
            return [.. mapping
                .Distinct()
                .OrderBy(x => x.biliIndex)];
        }
        public AlignmentResult Evaluate(IReadOnlyList<SubtitleItem> bilibiliSubs, IReadOnlyList<SubtitleItem> externalSubs)
        {
            if (bilibiliSubs == null || bilibiliSubs.Count == 0)
                throw new ArgumentException("B站字幕不能为空");
            if (externalSubs == null || externalSubs.Count == 0)
                return new AlignmentResult { TextScore = 0, TimeScore = 0, IsMatch = false };

            var biliTokens = TokenizeAndPhoneticize(bilibiliSubs);
            var extTokens = TokenizeAndPhoneticize(externalSubs);
            if (biliTokens.Count == 0 || extTokens.Count == 0)
                return new AlignmentResult { TextScore = 0, TimeScore = 0, IsMatch = false };

            var segments = BuildSegments(bilibiliSubs, externalSubs);
            if (segments.Count == 0)
                return new AlignmentResult { TextScore = 0, TimeScore = 0, IsMatch = false };

            var segResults = new List<SegmentAlignmentInfo>();
            double totalWeightedMatch = 0, totalTokensWeight = 0;
            var offsets = new List<double>();
            var offsetWeights = new List<double>();

            foreach (var seg in segments)
            {
                var bSegTokens = biliTokens.Where(t => t.Time >= seg.BStartSec && t.Time <= seg.BEndSec).ToList();
                var eSegTokens = extTokens.Where(t => t.Time >= seg.EStartSec && t.Time <= seg.EEndSec).ToList();
                if (bSegTokens.Count == 0 || eSegTokens.Count == 0)
                {
                    segResults.Add(new SegmentAlignmentInfo
                    {
                        BStartSec = seg.BStartSec, BEndSec = seg.BEndSec,
                        EStartSec = seg.EStartSec, EEndSec = seg.EEndSec,
                        BilibiliLineCount = seg.BLines.Count, ExternalLineCount = seg.ELines.Count,
                        NormalizedCost = 1.0, SegmentOffset = double.NaN
                    });
                    continue;
                }

                var bFeatures = BuildFeatureVectors(bSegTokens);
                var eFeatures = BuildFeatureVectors(eSegTokens);
                var bSeq = bFeatures.Select(f => _options.TextWeight * f[0] + _options.TimeWeight * f[1]).ToArray();
                var eSeq = eFeatures.Select(f => _options.TextWeight * f[0] + _options.TimeWeight * f[1]).ToArray();
                var dtw = new Dtw(bSeq, eSeq);

                double cost = dtw.GetCost();
                var path = dtw.GetPath();
                int pathLen = path.Length;
                double maxStepCost = _options.TextWeight + _options.TimeWeight;
                double normalizedCost = Math.Min(cost / (pathLen * maxStepCost), 1.0);

                double segOffset = EstimateOffsetFromPath(path, bSegTokens, eSegTokens);
                if (!double.IsNaN(segOffset))
                {
                    offsets.Add(segOffset);
                    offsetWeights.Add(bSegTokens.Count);
                }

                segResults.Add(new SegmentAlignmentInfo
                {
                    BStartSec = seg.BStartSec, BEndSec = seg.BEndSec,
                    EStartSec = seg.EStartSec, EEndSec = seg.EEndSec,
                    BilibiliLineCount = seg.BLines.Count, ExternalLineCount = seg.ELines.Count,
                    NormalizedCost = normalizedCost, SegmentOffset = segOffset
                });
                totalWeightedMatch += (1 - normalizedCost) * bSegTokens.Count;
                totalTokensWeight += bSegTokens.Count;
            }

            double textScore = totalTokensWeight > 0 ? totalWeightedMatch / totalTokensWeight : 0;
            double overallOffset = offsets.Count > 0 ? WeightedAverage(offsets, offsetWeights) : double.NaN;
            double timeScore = 0;
            if (!double.IsNaN(overallOffset))
                timeScore = Math.Max(0, 1 - Math.Abs(overallOffset) / _options.MaxOffsetForTimeScore);
            double weightedScore = textScore * _options.TextScoreWeight + timeScore * _options.TimeScoreWeight;
            bool isMatch = weightedScore >= _options.MatchThreshold;

            return new AlignmentResult
            {
                TextScore = textScore,
                TimeScore = timeScore,
                WeightedOverallScore = weightedScore,   // 新加权分
                EstimatedOffsetSeconds = overallOffset,
                IsMatch = isMatch,
                Segments = segResults
            };
        }

        private static List<TimedToken> TokenizeAndPhoneticize(IReadOnlyList<SubtitleItem> subs)
        {
            var tokens = new List<TimedToken>();
            var wordRegex = new Regex(@"[a-zA-Z]+|[\u4e00-\u9fff]");
            for (int i = 0; i < subs.Count; i++)
            {
                var sub = subs[i];
                double midTime = (sub.StartSeconds + sub.EndSeconds) / 2.0;
                foreach (Match m in wordRegex.Matches(sub.Text))
                {
                    string raw = m.Value, processed;
                    if (raw.Length == 1 && Regex.IsMatch(raw, @"[\u4e00-\u9fff]"))
                    {
                        var pinyin = Pinyin.GetPinyin(raw[0]);
                        processed = Regex.Replace(pinyin, @"\d", "").ToLower();
                        if (string.IsNullOrEmpty(processed)) processed = raw;
                    }
                    else processed = raw.ToLowerInvariant();
                    if (processed.Length > 0)
                        tokens.Add(new TimedToken { ProcessedText = processed, Time = midTime, LineIndex = i });
                }
            }
            return tokens;
        }

        private static List<double[]> BuildFeatureVectors(List<TimedToken> tokens)
        {
            double minTime = tokens.Min(t => t.Time), maxTime = tokens.Max(t => t.Time);
            double timeRange = maxTime - minTime == 0 ? 1 : maxTime - minTime;
            var textIdMap = new Dictionary<string, double>();
            foreach (var token in tokens)
                if (!textIdMap.ContainsKey(token.ProcessedText))
                    textIdMap[token.ProcessedText] = Math.Abs(token.ProcessedText.GetHashCode()) / (double)int.MaxValue;
            return tokens.Select(token => new double[] { textIdMap[token.ProcessedText], (token.Time - minTime) / timeRange }).ToList();
        }

        private List<SegmentRange> BuildSegments(IReadOnlyList<SubtitleItem> bilibiliSubs, IReadOnlyList<SubtitleItem> externalSubs)
        {
            var segments = new List<SegmentRange>();
            if (bilibiliSubs.Count == 0) return segments;

            var bGroups = new List<List<SubtitleItem>>();
            var current = new List<SubtitleItem> { bilibiliSubs[0] };
            for (int i = 1; i < bilibiliSubs.Count; i++)
            {
                double gap = bilibiliSubs[i].StartSeconds - bilibiliSubs[i - 1].EndSeconds;
                if (gap >= _options.SegmentGapThreshold)
                {
                    bGroups.Add(current);
                    current = new List<SubtitleItem>();
                }
                current.Add(bilibiliSubs[i]);
            }
            if (current.Count > 0) bGroups.Add(current);

            foreach (var bGroup in bGroups)
            {
                double bStart = bGroup.First().StartSeconds - _options.SearchWindowSeconds;
                double bEnd = bGroup.Last().EndSeconds + _options.SearchWindowSeconds;
                var eGroup = externalSubs.Where(e => e.StartSeconds >= bStart && e.EndSeconds <= bEnd).ToList();
                if (eGroup.Count == 0) eGroup = externalSubs.ToList();
                double eStart = eGroup.Min(e => e.StartSeconds) - _options.SearchWindowSeconds;
                double eEnd = eGroup.Max(e => e.EndSeconds) + _options.SearchWindowSeconds;
                segments.Add(new SegmentRange
                {
                    BStartSec = Math.Max(0, bStart), BEndSec = bEnd,
                    EStartSec = Math.Max(0, eStart), EEndSec = eEnd,
                    BLines = bGroup, ELines = eGroup
                });
            }
            return segments;
        }

        private static double EstimateOffsetFromPath(Tuple<int, int>[] path, List<TimedToken> bTokens, List<TimedToken> eTokens)
        {
            var diffs = new List<double>();
            foreach (var t in path)
            {
                if (t.Item1 >= 0 && t.Item1 < bTokens.Count && t.Item2 >= 0 && t.Item2 < eTokens.Count)
                    diffs.Add(eTokens[t.Item2].Time - bTokens[t.Item1].Time);
            }
            if (diffs.Count == 0) return double.NaN;
            diffs.Sort();
            return diffs[diffs.Count / 2];
        }

        private static double WeightedAverage(List<double> values, List<double> weights)
        {
            double sum = 0, wSum = 0;
            for (int i = 0; i < values.Count; i++) { sum += values[i] * weights[i]; wSum += weights[i]; }
            return wSum > 0 ? sum / wSum : double.NaN;
        }
    }
}