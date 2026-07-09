using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Godot;

public static class SubtitleUtils
{
    private static Dictionary<string, string> _t2sDict;
    private static readonly object _dictLock = new();

    private static readonly string[] ForbiddenKeywords = {
        "未经授权", "侵权必究", "版权", "盗版", "仅供学习", "侵删", "转载请注明",
        "版权所有", "禁止转载", "严禁转载", "未经许可", "不得转载",
        "不得用于商业用途", "禁止商用", "非商业用途", "违者必究",
        "追究法律责任", "如有侵权", "联系删除", "请告知删除",
        "版权归原作者所有", "仅供学习交流", "转载自", "来源网络",
        "翻唱翻录", "不得翻唱", "不得翻录", "翻录必究", "著作权",
        "不得使用", "未经著作权人许可",
        "copyright", "all rights reserved", "infringement", "unauthorized",
        "pirated", "for learning only", "remove if infringement",
        "please indicate the source", "no reproduction", "permission required",
        "commercial use prohibited", "not for redistribution", "do not copy",
        "unauthorized use", "dmca", "takedown", "copyright infringement",
        "no copying", "do not reproduce", "legal action", "violators will be prosecuted"
    };

    public static void LoadTSDictionary()
    {
        if (_t2sDict != null) return;
        lock (_dictLock)
        {
            if (_t2sDict != null) return;
            _t2sDict = new Dictionary<string, string>(6000);
            string path = ProjectSettings.GlobalizePath("res://Data/TSCharacters.txt");
            try
            {
                using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
                if (file == null)
                {
                    GD.PrintErr($"[TSDictionary] 无法打开词典文件: {path}");
                    return;
                }
                while (!file.EofReached())
                {
                    string line = file.GetLine().Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length >= 2 && parts[0].Length == 1 && parts[1].Length > 0)
                    {
                        string simplified = parts[1].Split(' ')[0];
                        _t2sDict[parts[0]] = simplified;
                    }
                }
                GD.Print($"[TSDictionary] 加载完成，共 {_t2sDict.Count} 条映射");
            }
            catch (Exception e) { GD.PrintErr($"[TSDictionary] 加载失败: {e.Message}"); }
        }
    }

    public static string ToSimplified(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (_t2sDict == null) LoadTSDictionary();
        if (_t2sDict == null || _t2sDict.Count == 0) return text;

        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (_t2sDict.TryGetValue(c.ToString(), out string simplified))
                sb.Append(simplified);
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    public static string NormalizeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = ToSimplified(text);
        text = Regex.Replace(text, @"\s+", "");
        text = Regex.Replace(text, @"[\p{P}\p{S}]", "");
        return text;
    }

    public static double CalculateSimilarity(string a, string b)
    {
        a = NormalizeText(a);
        b = NormalizeText(b);
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;

        if (a.Length < 2 || b.Length < 2)
            return a == b ? 1.0 : 0.0;

        var setA = new HashSet<string>();
        var setB = new HashSet<string>();
        for (int i = 0; i < a.Length - 1; i++) setA.Add(a.Substring(i, 2));
        for (int i = 0; i < b.Length - 1; i++) setB.Add(b.Substring(i, 2));

        int inter = setA.Intersect(setB).Count();
        int union = setA.Union(setB).Count();
        return union == 0 ? 0 : (double)inter / union;
    }

    public static List<(double time, string text)> AlignBilibiliToExternalMulti(
        List<string> externalLines,
        List<(double time, string text)> bilibiliEntries,
        double similarityThreshold = 0.4,
        double fallbackThreshold = 0.3)
    {
        GD.Print("[DEBUG-ALIGN] ========== 开始最终对齐（跳过前奏） ==========");
        if (externalLines == null || externalLines.Count == 0)
            return new List<(double time, string text)>();
        if (bilibiliEntries == null || bilibiliEntries.Count == 0)
            return externalLines.Select(line => (0.0, line)).ToList();

        var biliItems = bilibiliEntries
            .Select(x => (time: x.time, norm: NormalizeText(x.text), raw: x.text))
            .Where(x => !string.IsNullOrWhiteSpace(x.norm) && x.norm.Length >= 2
                        && !Regex.IsMatch(x.norm, @"^[\d\.\,\;\:\!?\-\+\(\)\[\]\{\}\s]+$"))
            .ToList();

        int nExt = externalLines.Count;
        int nBili = biliItems.Count;
        var result = new List<(double time, string text)>(nExt);
        var usedBiliIndices = new HashSet<int>();
        double lastMatchedTime = -1.0;

        int globalStartIdx = 0;
        if (externalLines.Count > 0)
        {
            string firstExtNorm = NormalizeText(externalLines[0]);
            for (int k = 0; k < nBili; k++)
            {
                if (CalculateSimilarity(firstExtNorm, biliItems[k].norm) > 0.2)
                {
                    globalStartIdx = k;
                    break;
                }
            }
            if (globalStartIdx > 0)
            {
                lastMatchedTime = biliItems[globalStartIdx - 1].time;
                GD.Print($"   => 跳过前奏，从 B站[{globalStartIdx}] 开始 ({biliItems[globalStartIdx].time:F2}s)");
            }
        }

        for (int i = 0; i < nExt; i++)
        {
            string extNorm = NormalizeText(externalLines[i]);
            GD.Print($"\n[DEBUG-ALIGN] 外部[{i}] '{externalLines[i]}'");

            double bestSimUnused = 0;
            int bestStartUnused = -1, bestEndUnused = -1;

            for (int k = 0; k < nBili; k++)
            {
                if (usedBiliIndices.Contains(k)) continue;
                if (biliItems[k].time <= lastMatchedTime) continue;

                var merged = new StringBuilder();
                int end = k;
                while (end < nBili && !usedBiliIndices.Contains(end))
                {
                    if (merged.Length > 0) merged.Append(" ");
                    merged.Append(biliItems[end].norm);
                    double sim = CalculateSimilarity(extNorm, merged.ToString());
                    if (sim >= similarityThreshold)
                    {
                        if (bestStartUnused == -1 || k < bestStartUnused || (k == bestStartUnused && sim > bestSimUnused))
                        {
                            bestSimUnused = sim;
                            bestStartUnused = k;
                            bestEndUnused = end;
                        }
                    }
                    else if (sim > bestSimUnused && bestStartUnused == -1)
                    {
                        bestSimUnused = sim;
                        bestStartUnused = k;
                        bestEndUnused = end;
                    }
                    if (merged.Length > extNorm.Length * 2.5 && end > k) break;
                    end++;
                }
            }

            if (bestStartUnused >= 0 && bestSimUnused >= similarityThreshold)
            {
                result.Add((biliItems[bestStartUnused].time, externalLines[i]));
                for (int u = bestStartUnused; u <= bestEndUnused; u++)
                    usedBiliIndices.Add(u);
                lastMatchedTime = biliItems[bestStartUnused].time;
                GD.Print($"   => 匹配未使用 B站[{bestStartUnused}..{bestEndUnused}] 时间={biliItems[bestStartUnused].time:F2}s sim={bestSimUnused:F3}");
                continue;
            }

            if (bestStartUnused >= 0 && bestSimUnused >= fallbackThreshold)
            {
                result.Add((biliItems[bestStartUnused].time, externalLines[i]));
                for (int u = bestStartUnused; u <= bestEndUnused; u++)
                    usedBiliIndices.Add(u);
                lastMatchedTime = biliItems[bestStartUnused].time;
                GD.Print($"   => Fallback未使用 B站[{bestStartUnused}..{bestEndUnused}] 时间={biliItems[bestStartUnused].time:F2}s sim={bestSimUnused:F3}");
                continue;
            }

            double bestSimReuse = 0;
            int bestStartReuse = -1, bestEndReuse = -1;
            for (int k = 0; k < nBili; k++)
            {
                if (biliItems[k].time < lastMatchedTime) continue;

                var merged = new StringBuilder();
                int end = k;
                while (end < nBili)
                {
                    if (merged.Length > 0) merged.Append(" ");
                    merged.Append(biliItems[end].norm);
                    double sim = CalculateSimilarity(extNorm, merged.ToString());
                    if (sim >= similarityThreshold)
                    {
                        if (bestStartReuse == -1 || k < bestStartReuse || (k == bestStartReuse && sim > bestSimReuse))
                        {
                            bestSimReuse = sim;
                            bestStartReuse = k;
                            bestEndReuse = end;
                        }
                    }
                    else if (sim > bestSimReuse && bestStartReuse == -1)
                    {
                        bestSimReuse = sim;
                        bestStartReuse = k;
                        bestEndReuse = end;
                    }
                    if (merged.Length > extNorm.Length * 2.5 && end > k) break;
                    end++;
                }
            }

            if (bestStartReuse >= 0 && bestSimReuse >= similarityThreshold)
            {
                result.Add((biliItems[bestStartReuse].time, externalLines[i]));
                lastMatchedTime = biliItems[bestStartReuse].time;
                GD.Print($"   => 匹配重用 B站[{bestStartReuse}..{bestEndReuse}] 时间={biliItems[bestStartReuse].time:F2}s sim={bestSimReuse:F3}");
            }
            else if (bestStartReuse >= 0 && bestSimReuse >= fallbackThreshold)
            {
                result.Add((biliItems[bestStartReuse].time, externalLines[i]));
                lastMatchedTime = biliItems[bestStartReuse].time;
                GD.Print($"   => Fallback重用 B站[{bestStartReuse}..{bestEndReuse}] 时间={biliItems[bestStartReuse].time:F2}s sim={bestSimReuse:F3}");
            }
            else
            {
                double interpTime;
                if (result.Count == 0 && globalStartIdx > 0)
                    interpTime = biliItems[globalStartIdx].time;
                else
                    interpTime = InterpolateTime(i, result, bilibiliEntries, externalLines);

                result.Add((interpTime, externalLines[i]));
                GD.Print($"   => 无达标匹配 (未使用best={bestSimUnused:F3}, 重用best={bestSimReuse:F3})，插值时间={interpTime:F2}s");
            }
        }

        return result;
    }

    private static double InterpolateTime(int index,
        List<(double time, string text)> alignedSoFar,
        List<(double time, string text)> bilibiliEntries,
        List<string> externalLines)
    {
        int prevIndex = -1;
        for (int k = index - 1; k >= 0; k--)
        {
            if (k < alignedSoFar.Count && alignedSoFar[k].time >= 0)
            {
                prevIndex = k;
                break;
            }
        }

        if (prevIndex >= 0)
        {
            double prevTime = alignedSoFar[prevIndex].time;
            double avgInterval = 3.0;
            int matchedCount = 0;
            double totalInterval = 0;
            for (int m = 1; m < alignedSoFar.Count; m++)
            {
                double diff = alignedSoFar[m].time - alignedSoFar[m - 1].time;
                if (diff > 0 && diff < 30)
                {
                    totalInterval += diff;
                    matchedCount++;
                }
            }
            if (matchedCount > 0)
                avgInterval = totalInterval / matchedCount;

            int lineDiff = index - prevIndex;
            return prevTime + lineDiff * avgInterval;
        }

        if (bilibiliEntries.Count > 0)
            return bilibiliEntries[0].time;
        return 0;
    }

    public static bool ContainsTimestamps(string lrc) =>
        Regex.IsMatch(lrc, @"\[\d{2}:\d{2}\.\d{2,3}\]");

    // ========================= 带 DEBUG 输出的 CleanLrcMeta =========================
    public static string CleanLrcMeta(string lrcContent)
    {
        GD.Print("[DEBUG-LRC] ========== CleanLrcMeta 开始 ==========");
        var lines = lrcContent.Split('\n');
        var sb = new StringBuilder();
        int lineNum = 0;

        foreach (var rawLine in lines)
        {
            lineNum++;
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
            {
                GD.Print($"  [行{lineNum}] 空行，跳过");
                continue;
            }

            // 1. 检查标准元数据头
            if (Regex.IsMatch(line, @"^\[(ti|ar|al|by|offset|length):", RegexOptions.IgnoreCase))
            {
                GD.Print($"  [行{lineNum}] 标准LRC元数据头，跳过: {line}");
                continue;
            }

            // 2. 提取时间标签后的文本
            var m = Regex.Match(line, @"^\[(\d{2}):(\d{2})\.(\d{2,3})\](.*)");
            if (!m.Success)
            {
                GD.Print($"  [行{lineNum}] 无合法时间标签，跳过: {line}");
                continue;
            }

            string text = m.Groups[4].Value.Trim();
            if (string.IsNullOrEmpty(text))
            {
                GD.Print($"  [行{lineNum}] 文本为空，跳过");
                continue;
            }

            // 3. 检查是否为元数据文本（版权/冒号等）
            bool isMeta = IsMetaDataLine(text);
            GD.Print($"  [行{lineNum}] 文本='{text}' | IsMetaDataLine={isMeta}");
            if (isMeta)
            {
                GD.Print($"    => 被过滤（元数据/版权）");
                continue;
            }

            // 通过检查，保留
            GD.Print($"    => 保留");
            sb.AppendLine(line);
        }

        string result = sb.ToString().Trim();
        GD.Print($"[DEBUG-LRC] CleanLrcMeta 结束，保留行数: {result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length}");
        return result;
    }

    // ========================= 带 DEBUG 输出的 ExtractLyricLinesFromLrc =========================
    public static List<string> ExtractLyricLinesFromLrc(string cleanedLrc)
    {
        GD.Print("[DEBUG-LRC] ========== ExtractLyricLinesFromLrc 开始 ==========");
        var lines = cleanedLrc.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var lyricList = new List<string>();
        int lineNum = 0;

        foreach (var line in lines)
        {
            lineNum++;
            var m = Regex.Match(line, @"^\[\d{2}:\d{2}\.\d{2,3}\](.*)");
            if (!m.Success)
            {
                GD.Print($"  [行{lineNum}] 无合法时间戳，跳过: {line}");
                continue;
            }

            string text = m.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(text))
            {
                GD.Print($"  [行{lineNum}] 文本为空，跳过");
                continue;
            }

            bool isMeta = IsMetaDataLine(text);
            GD.Print($"  [行{lineNum}] 文本='{text}' | IsMetaDataLine={isMeta}");
            if (isMeta)
            {
                GD.Print($"    => 被过滤（元数据/版权）");
                continue;
            }

            GD.Print($"    => 添加");
            lyricList.Add(text);
        }

        // 移除可能的标题行（如 "歌名 - 歌手"）
        if (lyricList.Count > 0 && lyricList[0].Contains(" - ") && lyricList[0].Length < 80)
        {
            GD.Print($"  [DEBUG-LRC] 疑似标题行，移除首行: '{lyricList[0]}'");
            lyricList.RemoveAt(0);
        }

        GD.Print($"[DEBUG-LRC] ExtractLyricLinesFromLrc 结束，歌词行数: {lyricList.Count}");
        return lyricList;
    }

    public static bool IsSameLanguage(string textA, string textB)
    {
        static double ChineseRatio(string s)
        {
            int cnt = 0, total = 0;
            foreach (char c in s)
            {
                if (c >= 0x4e00 && c <= 0x9fff) cnt++;
                total++;
            }
            return total == 0 ? 0 : (double)cnt / total;
        }

        double rA = ChineseRatio(textA);
        double rB = ChineseRatio(textB);
        bool aIsChinese = rA > 0.5;
        bool bIsChinese = rB > 0.5;
        return aIsChinese == bIsChinese;
    }

    private static string StripPunctuationAndSpace(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return Regex.Replace(input, @"[^\w\u4e00-\u9fff]", "");
    }

    private static bool IsMetaDataLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;

        // 含中英文冒号直接视为元数据
        if (text.Contains(':') || text.Contains('：'))
            return true;

        // 清理标点符号后检查关键词（忽略大小写）
        string cleaned = StripPunctuationAndSpace(text).ToLowerInvariant();

        foreach (var keyword in ForbiddenKeywords)
        {
            // 关键词也进行同样清理（确保没有特殊符号）
            string cleanedKeyword = StripPunctuationAndSpace(keyword).ToLowerInvariant();
            if (string.IsNullOrEmpty(cleanedKeyword)) continue;

            if (cleaned.Contains(cleanedKeyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}