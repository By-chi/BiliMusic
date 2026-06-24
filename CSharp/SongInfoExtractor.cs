using Godot;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

public static class SongInfoExtractor
{
	// ======================== 样本数据 ========================
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

	// ======================== 通用标签黑名单 ========================
	private static readonly HashSet<string> GenericTags = new(StringComparer.OrdinalIgnoreCase)
	{
		"全程高燃", "综漫", "节奏向", "励志", "励志AMV", "AMV", "MAD", "混剪", "纯音", "日推",
		"4K", "1080P", "Hi-Res", "无损", "完整版", "MV", "PV", "OP", "ED", "OST",
		"翻唱", "Cover", "Remix", "伴奏", "inst.", "剪辑", "片段",
		"高燃", "燃向", "催泪", "治愈", "电音", "慢速版", "加速版", "现场",
		"试听", "耳机试听", "百万豪装", "母带", "重制", "杜比", "全景声"
	};

	private static bool ContainsGenericTag(string text)
	{
		// 1. 整个文本直接匹配标签（如“综漫”）
		if (GenericTags.Contains(text))
			return true;
		// 2. 按空格分词后逐一检查
		var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		foreach (var word in words)
		{
			if (GenericTags.Contains(word))
				return true;
		}
		return false;
	}

	// ======================== 主提取函数 ========================
	public static string ExtractSongName(string title)
	{
		try
		{
			if (string.IsNullOrEmpty(title))
				return "";

			// 全局花体规范化
			string cleanedTitle = NormalizeStylizedText(title).StripEdges();

			string bookResult = ExtractFromBookTitle(cleanedTitle);
			if (!string.IsNullOrEmpty(bookResult))
				return bookResult;

			if (cleanedTitle.Contains("「"))
			{
				var match = Regex.Match(cleanedTitle, @"「([^」]*)」");
				if (match.Success)
				{
					string inner = match.Groups[1].Value;
					inner = NormalizeStylizedText(inner);
					inner = Regex.Replace(inner, @"^[A-Za-z0-9]+\s*[：:]\s*", "");
					string cleaned = CleanAndValidate(inner);
					if (!string.IsNullOrEmpty(cleaned))
						return cleaned;
				}
			}

			string strippedOfPrefixes = StripCommonAudioPrefixes(cleanedTitle);
			if (!string.IsNullOrEmpty(strippedOfPrefixes))
				cleanedTitle = strippedOfPrefixes;

			EnsureSamplesLoaded();
			if (KnownSongNames != null && KnownSongNames.Contains(cleanedTitle))
				return cleanedTitle;
			if (KnownSingers != null && KnownSingers.Contains(cleanedTitle))
				return "";

			if (cleanedTitle.Contains("「▸」"))
			{
				string dolbyResult = ExtractFromDolbyFormat(cleanedTitle);
				if (!string.IsNullOrEmpty(dolbyResult))
					return dolbyResult;
			}

			string splitResult = TryMultipleSplits(cleanedTitle);
			if (!string.IsNullOrEmpty(splitResult))
				return splitResult;

			// 5. 全标题强力清洗（最后手段）
			string final = CleanString(cleanedTitle);
			if (!string.IsNullOrEmpty(final))
			{
				if (KnownSingers != null && KnownSingers.Contains(final))
					return "";
				if (KnownSongNames != null && KnownSongNames.Contains(final))
					return final;
				if (IsValidSongName(final) && !IsLikelyComment(final))
					return final;
			}

			return "";
		}
		catch (Exception e)
		{
			GD.PrintErr($"ExtractSongName failed: {e.Message}");
			return "";
		}
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

		string[] descriptiveKeywords = { "宣传曲", "主题曲", "印象曲", "插入曲", "片尾曲", "片头曲", "纪念", "周年" };
		bool isDescriptive = false;
		foreach (var kw in descriptiveKeywords)
		{
			if (left.Contains(kw))
			{
				isDescriptive = true;
				break;
			}
		}

		if (isDescriptive)
		{
			string desc = CleanDescription(left);
			if (!string.IsNullOrEmpty(desc))
				return desc;
		}

		// 常规歌曲格式：按 " - " 拆分
		if (left.Contains(" - "))
		{
			var dp = left.Split(" - ");
			// 尝试两侧，标签黑名单会自动过滤掉噪音段
			string r = TryCleanPart(dp[0].StripEdges());
			if (!string.IsNullOrEmpty(r)) return r;
			r = TryCleanPart(dp[^1].StripEdges());
			if (!string.IsNullOrEmpty(r)) return r;
		}
		return TryCleanPart(left);
	}

	private static string CleanDescription(string text)
	{
		if (string.IsNullOrEmpty(text)) return "";
		// 去除圆括号/书名号/方括号备注
		text = Regex.Replace(text, @"[（\(\[【].*?[）\)\]】]", " ");
		// 去除常见纯噪音标签
		text = Regex.Replace(text, @"\b(Official\s*Music\s*Video|Remastered|Music\s*Video|MV版|Official\s*Audio)\b", " ", RegexOptions.IgnoreCase);
		text = Regex.Replace(text, @"\s+", " ").Trim();
		// 去掉末尾标点
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

		var sepStrategies = new List<(string separator, bool leftFirst)>
		{
			(" - ", true), (" – ", true), (" — ", true),
			(" ~ ", false), ("～", false),
			(" / ", false), ("／", false),
			(" | ", false), ("｜", false),
			(" :: ", false), ("：：", false),
			(" → ", false), ("→", false), (" ➔ ", false),
			(" — ", true),
			("-", true), ("/", false), ("|", false), ("｜", false)
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

		// 样本优先
		if (KnownSongNames != null && KnownSongNames.Contains(trimmed))
			return trimmed;
		if (KnownSingers != null && KnownSingers.Contains(trimmed))
			return "";

		string cleaned = CleanString(part);
		if (string.IsNullOrEmpty(cleaned)) return "";

		// 排除歌手样本
		if (KnownSingers != null && KnownSingers.Contains(cleaned))
			return "";

		// ★ 排除通用标签（“综漫”、“励志AMV”等）★
		if (ContainsGenericTag(cleaned))
			return "";

		// 歌名样本再次确认
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
			if (ContainsGenericTag(simple)) return "";       // 标签过滤
			if (KnownSongNames != null && KnownSongNames.Contains(simple)) return simple;
			return IsValidSongName(simple) ? simple : "";
		}

		string cleaned = CleanString(candidate);
		if (string.IsNullOrEmpty(cleaned)) return "";
		if (KnownSingers != null && KnownSingers.Contains(cleaned)) return "";
		if (ContainsGenericTag(cleaned)) return "";          // 标签过滤
		if (KnownSongNames != null && KnownSongNames.Contains(cleaned)) return cleaned;
		return IsValidSongName(cleaned) ? cleaned : "";
	}

	// ── 完整噪音清洗（已移除主题曲/插曲等保护词）──
	public static string CleanString(string raw)
	{
		if (string.IsNullOrEmpty(raw)) return "";
		string s = raw;

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

	// ═══════════════ 噪音词库 ═══════════════
	private static readonly (string pattern, RegexOptions options)[] WordNoisePatterns = 
	{
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
	};

	private static readonly string[] SymbolNoise = 
	[
		"//", "+", "%%", " ’ ’", " ’ ", "×", " × ", "&amp;", "&", 
		"▸", "♪", "♫", "|", "｜", "《", "》", "—", ":", "：", "-"
	];
	private static readonly HashSet<string> ExternalNoiseWords = new(StringComparer.OrdinalIgnoreCase);

	private static string SimpleClean(string s)
	{
		s = s.Replace("《", "").Replace("》", "");
		s = s.Replace("【", "").Replace("】", "");
		s = s.Replace("「", "").Replace("」", "");
		s = s.Replace("『", "").Replace("』", "");
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
			// 禁止除逗号外的句子标点（逗号已被移出）
			if ("。！？；：、…—".Contains(c))
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
		// 长句且包含逗号/句号，仍视为评论
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
		return text.Normalize(NormalizationForm.FormKD);
	}

	private static string StripCommonAudioPrefixes(string title)
	{
		if (string.IsNullOrEmpty(title)) return "";

		string pattern1 = @"(在\s*)?百万豪装录音棚大声听\s*";
		string pattern2 = @"杜比全景声\s*[×xX×]\s*Hi[-]?Res?\s*[｜|]?\s*";
		string pattern3 = @"杜比全景声\s*\+\s*HIRES\s*[｜|]?\s*";

		string cleaned = Regex.Replace(title, pattern1, "", RegexOptions.IgnoreCase);
		cleaned = Regex.Replace(cleaned, pattern2, "", RegexOptions.IgnoreCase);
		cleaned = Regex.Replace(cleaned, pattern3, "", RegexOptions.IgnoreCase);
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
