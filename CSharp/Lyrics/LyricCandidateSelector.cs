using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;

/// <summary>一份已下载并打分的候选歌词</summary>
public class ScoredLyric
{
    public ILyricsSource Source;
    public SongInfo Song;
    public string CleanedLrc;
    public List<(double time, string text)> TimedLines;
    public double LastTimestamp;
    public double TextScore;
    public double DurationScore;
    public double TotalScore;
}


public static class LyricCandidateSelector
{
    /// <summary>全源搜索、按 歌名|歌手 去重</summary>
    public static async Task<List<(ILyricsSource source, SongInfo song)>> CollectCandidatesAsync(
        List<ILyricsSource> sources, string keyword, int maxPerSource = 10)
    {
        var seen = new HashSet<string>();
        var list = new List<(ILyricsSource, SongInfo)>();
        foreach (var src in sources)
        {
            try
            {
                var songs = await src.SearchAsync(keyword);
                if (songs == null) continue;
                foreach (var s in songs.Take(maxPerSource))
                    if (seen.Add($"{s.Name}|{s.Artist}"))
                        list.Add((src, s));
            }
            catch (Exception e) { GD.PrintErr($"[Selector] {src.GetType().Name} 搜索异常: {e.Message}"); }
        }
        return list;
    }

    /// <summary>
    /// 下载候选歌词并打分排序。
    ///
    /// 两条计分路径：
    /// - 有 AI 字幕（TextScore >= 0）：文本分是强证据
    ///     Total = 0.65×文本 + 0.35×时长 + 标题精确0.08 - 疑似翻唱0.10
    /// - 纯外部（无AI字幕）：时长是弱证据，结构信号主导
    ///     Total = 0.60×时长 + 标题精确0.20 + 歌手匹配0.15
    ///            - Live版(歌手未匹配)0.25 / (歌手匹配)0.10 - 疑似翻唱0.15
    ///
    /// 歌手提示来自 videoTitle：经 singer.txt 词库扫描提取（ExtractSingerHint），
    /// 提取失败时静默回退到无歌手先验的老行为。
    /// </summary>
    public static async Task<List<ScoredLyric>> ScoreCandidatesAsync(
        List<(ILyricsSource source, SongInfo song)> candidates,
        int downloadLimit,
        double? audioDuration,
        double tailThresholdSec,
        string aiSubtitleText,
        string keyword = null,
        string videoTitle = null)
    {
        var scored = new List<ScoredLyric>();
        int downloaded = 0, attempts = 0;
        const int MaxAttempts = 12;

        string normKeyword = TextSimilarity.Normalize(keyword ?? "");
        bool hasAiSubtitle = !string.IsNullOrEmpty(aiSubtitleText);

        string artistHint = SongInfoExtractor.ExtractSingerHint(videoTitle ?? "");
        if (!string.IsNullOrEmpty(artistHint))
            GD.Print($"[Selector] 歌手提示: {artistHint}（来自视频标题）");

        foreach (var (src, song) in candidates)
        {
            if (downloaded >= downloadLimit || attempts >= MaxAttempts) break;

            string lrc = null;
            try { lrc = await src.GetLyricAsync(song); } catch { /* 忽略单源失败 */ }
            attempts++;
            if (string.IsNullOrWhiteSpace(lrc)) continue;

            string cleaned = SubtitleUtils.CleanLrcMeta(lrc);
            var timed = ParseTimedLines(cleaned, song.Name);
            if (timed.Count < 2 || IsPlaceholder(timed)) continue;

            downloaded++;
            var item = new ScoredLyric
            {
                Source = src,
                Song = song,
                CleanedLrc = cleaned.Trim(),
                TimedLines = timed,
                LastTimestamp = timed[timed.Count - 1].time
            };

            if (audioDuration.HasValue && audioDuration.Value > 0)
            {
                double diff = Math.Abs(item.LastTimestamp - (audioDuration.Value - tailThresholdSec));
                item.DurationScore = Math.Max(0, 1 - diff / 45.0);
            }
            else item.DurationScore = 0.5;

            if (hasAiSubtitle)
                item.TextScore = TextSimilarity.DocumentScore(
                    aiSubtitleText, string.Join(" ", timed.Select(t => t.text)));
            else item.TextScore = -1;

            string normSongName = TextSimilarity.Normalize(song.Name ?? "");
            bool titleExact = normKeyword.Length >= 2 && normSongName == normKeyword;

            string metaText = $"{song.Name} {song.Artist} {lrc}";
            bool isCover = metaText.Contains("翻唱")
                        || metaText.Contains("cover", StringComparison.OrdinalIgnoreCase)
                        || metaText.Contains("原唱：")
                        || metaText.Contains("原唱:");

            // 版本弱化标记：归一化会去括号，必须在原文上查
            string rawName = song.Name ?? "";
            bool isLiveVersion = rawName.Contains("Live")
                            || rawName.Contains("live")
                            || rawName.Contains("现场")
                            || rawName.Contains("演唱会")
                            || rawName.Contains("巡演")
                            || rawName.Contains("音乐节")
                            || Regex.IsMatch(rawName, @"(Remix|Inst|伴奏|Demo)", RegexOptions.IgnoreCase);

            bool artistMatch = !string.IsNullOrEmpty(artistHint)
                && SongInfoExtractor.IsKnownSingerMatch(song.Artist, artistHint);

            if (hasAiSubtitle)
            {
                // 有AI字幕路径保持已验证的权重不动，歌手先验只做小幅加分
                item.TotalScore = 0.65 * item.TextScore + 0.35 * item.DurationScore;
                if (titleExact) item.TotalScore += 0.08;
                if (isCover)    item.TotalScore -= 0.10;
                if (artistMatch) item.TotalScore += 0.05;
            }
            else
            {
                item.TotalScore = 0.60 * item.DurationScore;
                if (titleExact)    item.TotalScore += 0.20;
                if (artistMatch)   item.TotalScore += 0.15;
                // Live惩罚分层：标题歌手匹配的Live轻罚（用户可能就在看演唱会），不匹配的重罚
                if (isLiveVersion) item.TotalScore -= artistMatch ? 0.10 : 0.25;
                if (isCover)       item.TotalScore -= 0.15;
            }

            scored.Add(item);
            GD.Print($"[Selector] 候选: {song.Name} - {song.Artist} | 末行{item.LastTimestamp:F1}s " +
                    $"文本{item.TextScore:F3} 时长{item.DurationScore:F3} 总分{item.TotalScore:F3}" +
                    $"{(titleExact ? " [标题精确]" : "")}{(artistMatch ? $" [歌手命中:{artistHint}]" : "")}" +
                    $"{(isCover ? " [疑似翻唱]" : "")}{(isLiveVersion ? " [Live版]" : "")}");
        }

        return scored.OrderByDescending(x => x.TotalScore).ToList();
    }




    private static List<(double time, string text)> ParseTimedLines(
        string cleanedLrc, string songName = null)
    {
        var list = new List<(double, string)>();
        foreach (var line in cleanedLrc.Split('\n'))
        {
            var m = Regex.Match(line, @"^\[(\d{1,2}):(\d{2})(?:\.(\d{1,3}))?\](.*)");
            if (!m.Success) continue;
            double t = int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value);
            if (m.Groups[3].Success)
                t += int.Parse(m.Groups[3].Value.PadRight(3, '0')) / 1000.0;
            string text = m.Groups[4].Value.Trim();
            if (text.Length == 0) continue;
            if (TextSimilarity.Normalize(text).Length == 0) continue;
            list.Add((t, text));
        }
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));

        // 过滤开头连续的标题头行（[00:00]歌名 - 歌手 之类）
        if (!string.IsNullOrEmpty(songName))
        {
            string normSong = TextSimilarity.Normalize(songName);
            while (list.Count > 0 && IsTitleHeaderLine(list[0].Item2, normSong))
                list.RemoveAt(0);
        }
        return list;
    }

    private static bool IsTitleHeaderLine(string text, string normSong)
    {
        if (normSong.Length < 2) return false;
        // 形如 "X - Y"（或 — –），整体较短，且 X 部分与歌名高度相似
        var m = Regex.Match(text, @"^(.{1,40}?)\s+[-—–]\s+");
        if (m.Success && text.Length <= 50)
        {
            if (TextSimilarity.ContainmentScore(normSong, TextSimilarity.Normalize(m.Groups[1].Value)) >= 0.8)
                return true;
        }
        // 整行恰好就是歌名
        if (TextSimilarity.Normalize(text) == normSong) return true;
        return false;
    }



    private static bool IsPlaceholder(List<(double time, string text)> lines)
    {
        string all = string.Join("", lines.Select(l => l.Item2));
        return all.Contains("暂无歌词") || all.Contains("纯音乐") || all.Contains("请欣赏")
            || all.Contains("暂时无法获取歌词") || all.Contains("此歌曲为没有填词的纯音乐");
    }
}