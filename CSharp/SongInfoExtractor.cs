using Godot;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

public static class SongInfoExtractor
{
    private static HashSet<string> KnownSongNames;
    private static HashSet<string> KnownSingers;
    private static bool samplesLoaded = false;
    private static readonly object loadLock = new();

    private static void EnsureSamplesLoaded()
    {
        if (samplesLoaded) return;
        lock (loadLock)
        {
            if (samplesLoaded) return;
            LoadFileIntoSet("res://Data/songName.txt", out KnownSongNames);
            LoadFileIntoSet("res://Data/singer.txt", out KnownSingers);
            samplesLoaded = true;
        }
    }

    private static void LoadFileIntoSet(string path, out HashSet<string> set)
    {
        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (file == null)
            {
                GD.PrintErr($"SongInfoExtractor: Cannot open {path}");
                return;
            }
            string content = file.GetAsText();
            content = content.Replace("\r\n", ",").Replace('\n', ',').Replace('\r', ',');
            var parts = content.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                string name = p.Trim();
                if (name.Length > 0)
                    set.Add(name);
            }
        }
        catch (Exception e)
        {
            GD.PrintErr($"SongInfoExtractor: Error loading {path}: {e.Message}");
        }
    }

    public static void ReloadSamples()
    {
        lock (loadLock)
        {
            samplesLoaded = false;
            KnownSongNames = null;
            KnownSingers = null;
        }
        EnsureSamplesLoaded();
    }

    private static readonly HashSet<string> GenericTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "全程高燃", "综漫", "节奏向", "励志", "励志AMV", "AMV", "MAD", "混剪", "纯音", "日推",
        "4K", "1080P", "Hi-Res", "无损", "完整版", "MV", "PV", "OP", "ED", "OST",
        "翻唱", "Cover", "Remix", "伴奏", "inst.", "剪辑", "片段",
        "高燃", "燃向", "催泪", "治愈", "电音", "慢速版", "加速版", "现场",
        "试听", "耳机试听", "百万豪装", "母带", "重制", "杜比", "全景声",
        "特别电影混音", "电影混音", "特别混音", "混音版", "宣传曲", "主题曲", "印象曲",
        "插入曲", "片尾曲", "片头曲", "完整版AMV", "Official MV", "Official PV",
        "沉浸声", "沉浸声卡拉OK", "FTSC沉浸声", "FANTASONIC", "IMMERSIVE", "KARAOKE"
    };

    private static bool ContainsGenericTag(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (GenericTags.Contains(text)) return true;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
            if (GenericTags.Contains(word)) return true;
        return false;
    }

    public static string ExtractSongName(string title)
    {
        try
        {
            if (string.IsNullOrEmpty(title)) return "";

            string cleanedTitle = NormalizeStylizedText(title).StripEdges();

            //  播放列表/纯评论拦截
            if (IsPlaylistTitle(cleanedTitle) || IsPureComment(cleanedTitle))
                return "";

            //  书名号提取
            string bookResult = ExtractFromBookTitle(cleanedTitle);
            if (!string.IsNullOrEmpty(bookResult))
                return bookResult;

            //  去除音频前缀
            string stripped = StripCommonAudioPrefixes(cleanedTitle);
            if (!string.IsNullOrEmpty(stripped))
                cleanedTitle = stripped;

            //  样本直接命中
            EnsureSamplesLoaded();
            if (KnownSongNames != null && KnownSongNames.Contains(cleanedTitle))
                return cleanedTitle;
            if (KnownSingers != null && KnownSingers.Contains(cleanedTitle))
                return "";

            // 杜比/特殊格式（含「▸」）
            if (cleanedTitle.Contains("「▸」"))
            {
                string dolbyResult = ExtractFromDolbyFormat(cleanedTitle);
                if (!string.IsNullOrEmpty(dolbyResult))
                    return dolbyResult;
            }

            //  多分隔符拆分（优化后优先右侧）
            string splitResult = TryMultipleSplits(cleanedTitle);
            if (!string.IsNullOrEmpty(splitResult))
                return splitResult;

            //  强力清洗兜底
            string final = CleanString(cleanedTitle);
            if (!string.IsNullOrEmpty(final))
            {
                if (KnownSingers != null && KnownSingers.Contains(final)) return "";
                if (KnownSongNames != null && KnownSongNames.Contains(final)) return final;
                if (IsValidSongName(final) && !IsLikelyComment(final)) return final;
            }

            // 最后尝试「」提取
            if (cleanedTitle.Contains("「"))
            {
                var match = Regex.Match(cleanedTitle, @"「([^」]*)」");
                if (match.Success)
                {
                    string inner = NormalizeStylizedText(match.Groups[1].Value);
                    inner = Regex.Replace(inner, @"^[A-Za-z0-9]+\s*[：:]\s*", "");
                    string cleanedInner = CleanAndValidate(inner);
                    if (!string.IsNullOrEmpty(cleanedInner))
                        return cleanedInner;
                }
            }

            return "";
        }
        catch (Exception e)
        {
            GD.PrintErr($"ExtractSongName failed: {e.Message}");
            return "";
        }
    }

    // 新增：纯评论/标题党拦截
    private static bool IsPureComment(string title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        // 无书名号/无「」/无英文单词，且长度>15，包含明显语气词
        if (!title.Contains("《") && !title.Contains("「") && title.Length > 15)
        {
            if (Regex.IsMatch(title, @"(难道|居然|竟然|不进来|不点开|后悔|跪下|震撼|太解气|迄今为止|本该如此)") &&
                title.Split(' ', ',', '，').Length > 5)
                return true;
        }
        return false;
    }

    private static string ExtractFromBookTitle(string title)
    {
        var innerList = new List<string>();
        int depth = 0, start = -1;
        for (int i = 0; i < title.Length; i++)
        {
            if (title[i] == '《')
            {
                if (depth == 0) start = i + 1;
                depth++;
            }
            else if (title[i] == '》')
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    string raw = title.Substring(start, i - start);
                    raw = NormalizeStylizedText(raw);
                    innerList.Add(raw);
                    start = -1;
                }
                if (depth < 0) depth = 0;
            }
        }

        foreach (string rawInner in innerList)
        {
            string cleaned = RemoveBracketedNotes(rawInner);
            string result = CleanAndValidate(cleaned);
            if (!string.IsNullOrEmpty(result))
                return result;
        }
        return "";
    }

    private static string RemoveBracketedNotes(string raw)
    {
        int prevLen;
        do
        {
            prevLen = raw.Length;
            raw = Regex.Replace(raw, "《[^》]*》", "");
            raw = Regex.Replace(raw, "[（\\(][^）\\)]*[）\\)]", "");
            raw = raw.StripEdges();
        } while (raw.Length != prevLen);
        return raw;
    }

    private static string ExtractFromDolbyFormat(string title)
    {
        var parts = title.Split("「▸」");
        if (parts.Length < 2) return "";
        string left = parts[0].StripEdges();
        left = Regex.Replace(left, @"^(杜比全景声\s*[×xX×]\s*Hi[-]?Res?\s*[｜|]?\s*)", "", RegexOptions.IgnoreCase);
        left = Regex.Replace(left, @"^(杜比全景声\s*\+\s*HIRES\s*[｜|]?\s*)", "", RegexOptions.IgnoreCase);
        left = left.StripEdges();

        // 描述性关键词检测
        string[] descriptiveKeywords = { "宣传曲", "主题曲", "印象曲", "插入曲", "片尾曲", "片头曲", "纪念", "周年" };
        bool isDescriptive = false;
        foreach (var kw in descriptiveKeywords)
            if (left.Contains(kw)) { isDescriptive = true; break; }

        if (isDescriptive)
        {
            string desc = CleanDescription(left);
            if (!string.IsNullOrEmpty(desc)) return desc;
        }

        // 按 " - " 拆分，优先取右侧（歌名）
        if (left.Contains(" - "))
        {
            var dp = left.Split(" - ");
            // 先尝试右侧，再左侧
            string r = TryCleanPart(dp[^1].StripEdges());
            if (!string.IsNullOrEmpty(r)) return r;
            r = TryCleanPart(dp[0].StripEdges());
            if (!string.IsNullOrEmpty(r)) return r;
        }
        return TryCleanPart(left);
    }

    private static string CleanDescription(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = Regex.Replace(text, @"[（\(\[【].*?[）\)\]】]", " ");
        text = Regex.Replace(text, @"\b(Official\s*Music\s*Video|Remastered|Music\s*Video|MV版|Official\s*Audio)\b", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length > 0 && ".…!？，;:".Contains(text[^1]))
            text = text[..^1].Trim();
        return IsValidSongName(text) ? text : "";
    }

    private static string TryMultipleSplits(string title)
    {
        if (string.IsNullOrEmpty(title)) return "";

        var forbiddenSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "杜比全景声", "dolby atmos", "ftsc沉浸式音频体验", "ftsc沉浸声",
            "百万豪装录音棚", "hires", "hdr", "drv重制版", "4k hdr",
            "官方mv", "the first take", "tft", "中日双语", "4k画质"
        };

        // 修改：对于 " - " 等常用分隔符，优先右侧（歌名通常在右）
        var sepStrategies = new List<(string separator, bool leftFirst)>
        {
            (" - ", false), (" – ", false), (" — ", false),
            (" ~ ", false), ("～", false),
            (" / ", false), ("／", false),
            (" | ", false), ("｜", false),
            (" :: ", false), ("：：", false),
            (" → ", false), ("→", false), (" ➔ ", false),
            ("-", false), ("/", false), ("|", false), ("｜", false)
        };

        foreach (var (sep, leftFirst) in sepStrategies)
        {
            if (title.Contains(sep))
            {
                string[] parts = title.Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries);
                int start = leftFirst ? 0 : parts.Length - 1;
                int end = leftFirst ? parts.Length : -1;
                int step = leftFirst ? 1 : -1;

                for (int i = start; i != end; i += step)
                {
                    string rawPart = parts[i].StripEdges();
                    if (forbiddenSegments.Contains(rawPart))
                        continue;

                    string candidate = TryCleanPart(rawPart);
                    if (!string.IsNullOrEmpty(candidate))
                        return candidate;
                }
            }
        }
        return "";
    }

    private static string TryCleanPart(string part)
    {
        if (string.IsNullOrEmpty(part)) return "";
        EnsureSamplesLoaded();
        string trimmed = part.StripEdges();

        if (KnownSongNames != null && KnownSongNames.Contains(trimmed))
            return trimmed;
        if (KnownSingers != null && KnownSingers.Contains(trimmed))
            return "";

        string cleaned = CleanString(part);
        if (string.IsNullOrEmpty(cleaned)) return "";

        if (KnownSingers != null && KnownSingers.Contains(cleaned))
            return "";
        if (ContainsGenericTag(cleaned))
            return "";
        if (KnownSongNames != null && KnownSongNames.Contains(cleaned))
            return cleaned;

        return IsValidSongName(cleaned) ? cleaned : "";
    }

    private static string CleanAndValidate(string candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return "";
        EnsureSamplesLoaded();
        string trimmed = candidate.StripEdges();

        if (KnownSongNames != null && KnownSongNames.Contains(trimmed))
            return trimmed;
        if (KnownSingers != null && KnownSingers.Contains(trimmed))
            return "";

        if (!ContainsChinese(candidate))
        {
            string simple = SimpleClean(candidate);
            if (string.IsNullOrEmpty(simple)) return "";
            if (KnownSingers != null && KnownSingers.Contains(simple)) return "";
            if (ContainsGenericTag(simple)) return "";
            if (KnownSongNames != null && KnownSongNames.Contains(simple)) return simple;
            return IsValidSongName(simple) ? simple : "";
        }

        string cleaned = CleanString(candidate);
        if (string.IsNullOrEmpty(cleaned)) return "";
        if (KnownSingers != null && KnownSingers.Contains(cleaned)) return "";
        if (ContainsGenericTag(cleaned)) return "";
        if (KnownSongNames != null && KnownSongNames.Contains(cleaned)) return cleaned;
        return IsValidSongName(cleaned) ? cleaned : "";
    }

    // ── 完整噪音清洗 ──
    public static string CleanString(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        string s = raw;

        // 统一移除各种引号
        s = s.Replace("\"", " ").Replace("\"", " ").Replace("\"", " ").Replace("\"", " ");
        s = s.Replace("'", " ").Replace("'", " ").Replace("‘", " ").Replace("’", " ");
        s = s.Replace("「", " ").Replace("」", " ").Replace("『", " ").Replace("』", " ");

        for (int i = 0; i < 3; i++)
        {
            s = Regex.Replace(s, @"[\[【].*?[\]】]", " ");
            s = Regex.Replace(s, @"[\(（].*?[\)）]", " ");
            s = Regex.Replace(s, @"《[^》]*》", " ");
        }

        s = Regex.Replace(s, @"[\uD800-\uDBFF][\uDC00-\uDFFF]|[\u2600-\u27BF]|[\u1F300-\u1F5FF]|[\u1F600-\u1F64F]|[\u1F680-\u1F6FF]|[\u1F1E0-\u1F1FF]", "");
        s = s.Replace("\u200b", "").Replace("\u200c", "").Replace("\u200d", "").Replace("\ufeff", "");
        s = s.Replace("　", " ");

        foreach (var (pattern, options) in WordNoisePatterns)
        {
            s = Regex.Replace(s, pattern, " ", options);
        }

        foreach (var sym in SymbolNoise)
        {
            s = s.Replace(sym, " ");
        }

        foreach (var word in ExternalNoiseWords)
        {
            string escaped = Regex.Escape(word);
            s = Regex.Replace(s, $@"\b{escaped}\b", " ", RegexOptions.IgnoreCase);
        }

        s = Regex.Replace(s, @"\bx\b", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+", " ").Trim();

        if (s.Length > 0 && ".…!？，;:".Contains(s[^1]))
            s = s[..^1].Trim();

        return s;
    }

    private static readonly (string pattern, RegexOptions options)[] WordNoisePatterns = 
    [
        (@"\b(无损|高音质|Hi-?Res|4K|1080[Pp]|DSD|Flac|Wav|MP3|320k|HQ|SQ)\b", RegexOptions.IgnoreCase),
        (@"\b(MV|PV|MAD|AMV|Official|Audio|AUDIO|Live|现场|Demo)\b", RegexOptions.IgnoreCase),
        (@"\b(翻唱|Cover|纯享|伴奏|inst\.|Remix|纯音乐|无人声|片段|剪辑)\b", RegexOptions.IgnoreCase),
        (@"\b(自制|原创|搬运|转载|字幕|动态歌词|AI翻唱|AI Cover)\b", RegexOptions.IgnoreCase),
        (@"\b(放松|学习|氛围|下雨|卧室|独处|冥想|通勤|旅行|发呆)\b", RegexOptions.IgnoreCase),
        (@"\b(播放列表|Playlist|循环|歌单|精选集|日推|月度)\b", RegexOptions.IgnoreCase),
        (@"\b(耳机试听|装备试听|极致音质|百万豪装|母带|还原|升频|降噪)\b", RegexOptions.IgnoreCase),
        (@"\b(完整版|短版|加快|减慢|变调|混音|重制|升调|降调|DJ版|Phonk)\b", RegexOptions.IgnoreCase),
        (@"\b(钢琴版|吉他版|原声|伴奏带|KTV|卡拉OK|演唱会|饭拍|综艺)\b", RegexOptions.IgnoreCase),
        (@"\b(预告|花絮|反应|Reaction|听歌|串烧|Medley|Mashup)\b", RegexOptions.IgnoreCase),
        (@"\b(1小时|睡眠|作业用|BGM|背景音乐)\b", RegexOptions.IgnoreCase),
        (@"\b(官方|完整|纯净|人声|提取|分离|AI变声|电音|重混|慢摇)\b", RegexOptions.IgnoreCase),
        // 年/届/周年整体移除（不留单字）
        (@"\d{2,4}\s*年", RegexOptions.IgnoreCase),
        (@"第?\d+\s*届", RegexOptions.IgnoreCase),
        (@"\d+\s*周年", RegexOptions.IgnoreCase),
        // 残留的孤立“年”“届”清理（前后为空格或标点）
        (@"(?<=\s)年(?=\s|$)", RegexOptions.IgnoreCase),
        (@"(?<=\s)届(?=\s|$)", RegexOptions.IgnoreCase),
    ];

    private static readonly string[] SymbolNoise = 
    [
        "//", "+", "%%", " ’ ’", " ’ ", "×", " × ", "&amp;", "&",
        "▸", "♪", "♫", "|", "｜", "《", "》", "—", ":", "：", "-",
        // 引号已在 CleanString 开头统一处理，此处保留以防遗漏
        "\"", "\"", "'", "'", "‘", "’", "·"
    ];
    private static readonly HashSet<string> ExternalNoiseWords = new(StringComparer.OrdinalIgnoreCase);

    private static string SimpleClean(string s)
    {
        // 统一引号移除
        s = s.Replace("\"", " ").Replace("\"", " ").Replace("'", " ").Replace("'", " ");
        s = s.Replace("「", " ").Replace("」", " ").Replace("『", " ").Replace("』", " ");

        s = s.Replace("《", "").Replace("》", "");
        s = s.Replace("【", "").Replace("】", "");
        s = s.Replace("|", " ").Replace("｜", " ");
        s = s.Replace("-", " ").Replace("—", " ");
        s = s.Replace(":", " ").Replace("：", " ");
        s = s.Replace("▸", " ");
        s = s.Replace("♪", " ").Replace("♫", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();

        if (s.Length > 0 && ".…!？，;:".Contains(s[^1]))
            s = s[..^1].Trim();
        return s;
    }

    private static bool ContainsChinese(string text)
    {
        foreach (char c in text)
            if ((c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0x3040 && c <= 0x30FF))
                return true;
        return false;
    }

    public static bool IsValidSongName(string name)
	{
		if (string.IsNullOrEmpty(name)) return false;
		if (name.Length < 2) return false;

		foreach (char c in name)
		{
			// 放行常见合法标点（包括英文句点）
			if (c == '，' || c == ',' || c == '！' || c == '？' || c == '：' || c == '～' ||
				c == '♪' || c == '♫' || c == '“' || c == '”' || c == '‘' || c == '’' || c == '.')
				continue;

			// 禁止其他句子标点
			if ("。；、…—".Contains(c))
				return false;

			if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || 
				c == '\'' || c == '-' || c == '&' || c == '·' || c == '♯' || c == '♭')
				continue;

			if ((c >= 0x4E00 && c <= 0x9FFF) ||
				(c >= 0x3040 && c <= 0x30FF) ||
				(c >= 0xAC00 && c <= 0xD7AF) ||
				(c >= 0x0400 && c <= 0x04FF) ||
				(c >= 0x0370 && c <= 0x03FF))
				continue;

			if (c == '\u3000' || c == '\u00A0' || c == '\u2022')
				continue;

			return false;
		}
		return true;
	}

    private static bool IsLikelyComment(string text)
    {
        if (text.Length > 12 && (text.Contains('，') || text.Contains('。')))
            return true;
        return false;
    }

    private static bool IsPlaylistTitle(string title)
    {
        string normalized = NormalizeStylizedText(title).ToLowerInvariant();

        string[] playlistKeywords = {
            "playlist", "playtlist", "歌单", "精选歌单", "氛围感歌单", "循环歌单",
            "日推歌单", "月度歌单", "播放列表", "英语歌单", "日语歌单", "韩语歌单",
            "欧美歌单", "精选合集", "音乐推荐", "电台新星", "周榜", "月榜", "歌单分享",
            "最佳歌单", "心情歌单", "场景歌单", "学习歌单", "工作歌单", "运动歌单"
        };
        foreach (var kw in playlistKeywords)
            if (normalized.Contains(kw)) return true;

        int separatorCount = Regex.Matches(title, @"[|｜\-—/]").Count;
        if (separatorCount >= 2)
        {
            string[] moodTags = {
                "放松", "学习", "氛围", "下雨", "卧室", "独处", "冥想", "通勤", "宅家",
                "假期", "旅行", "发呆", "雨季", "忧郁", "孤独", "浪漫", "深夜", "早晨",
                "咖啡", "阅读", "瑜伽", "自然", "海边", "森林", "星空"
            };
            int tagHit = 0;
            foreach (var tag in moodTags)
                if (normalized.Contains(tag)) tagHit++;
            if (tagHit >= 2) return true;
        }

        return false;
    }

    private static string NormalizeStylizedText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // 全角字母数字转换
        text = Regex.Replace(text, @"[０-９]", m => ((char)(m.Value[0] - '０' + '0')).ToString());
        text = Regex.Replace(text, @"[ａ-ｚ]", m => ((char)(m.Value[0] - 'ａ' + 'a')).ToString());
        text = Regex.Replace(text, @"[Ａ-Ｚ]", m => ((char)(m.Value[0] - 'Ａ' + 'A')).ToString());
        return text.Normalize(NormalizationForm.FormKD);
    }

    private static string StripCommonAudioPrefixes(string title)
    {
        if (string.IsNullOrEmpty(title)) return "";

        string[] patterns = {
            @"(在\s*)?百万豪装录音棚大声听\s*",
            @"杜比全景声\s*[×xX×+]\s*(Hi[-]?Res?\s*)?[｜|:：]?\s*",
            @"杜比全景声\s*[+×]\s*\S+\s*[-–]\s*",
            @"【?4KMAD\s+HIRES\s+\d+\]?\s*",
            @"杜比全景声\s*[+×]\s*HIRES\s*[｜|]?\s*",
            @"(沉浸声卡拉OK|FTSC\s*沉浸声|FANTASONIC\s*IMMERSIVE\s*KARAOKE)\s*[：:]\s*"
        };

        string cleaned = title;
        foreach (var pattern in patterns)
            cleaned = Regex.Replace(cleaned, pattern, "", RegexOptions.IgnoreCase);

        return cleaned.StripEdges();
    }

    public static Godot.Collections.Dictionary<string, string> ExtractFeatures(string title)
    {
        var f = new Godot.Collections.Dictionary<string, string>
        {
            ["song"] = ExtractSongName(title) ?? "",
            ["singer"] = ""
        };
        return f;
    }

    public static Godot.Collections.Array<Godot.Collections.Dictionary<string, string>> ExtractBatch(IEnumerable<string> titles)
    {
        var arr = new Godot.Collections.Array<Godot.Collections.Dictionary<string, string>>();
        foreach (var t in titles)
            arr.Add(ExtractFeatures(t));
        return arr;
    }
}