using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;

public class LyricsFetcher
{
    private readonly List<ILyricsSource> _sources;

    // 累计统计（线程安全）
    private static readonly Dictionary<string, (int attempts, int successes, float accuracy)> _sourceStats = new();
    private static readonly object _statsLock = new();
    private const string StatsJsonPath = "user://lyrics_fetcher_stats.json";

    // 静态构造函数：首次加载时从文件恢复历史统计
    static LyricsFetcher()
    {
        LoadStatsFromFile();
    }

    public LyricsFetcher(System.Net.Http.HttpClient http, params ILyricsSource[] sources)
    {
        _sources = sources?.ToList() ?? new List<ILyricsSource>();
    }

    /// <summary>
    /// 验证歌词内容是否有效（非占位文本且至少包含两行实际歌词）
    /// </summary>
    private bool IsLyricContentValid(string rawLrc)
    {
        if (string.IsNullOrWhiteSpace(rawLrc))
            return false;

        string cleaned = SubtitleUtils.CleanLrcMeta(rawLrc);
        if (string.IsNullOrWhiteSpace(cleaned))
            return false;

        List<string> lines = SubtitleUtils.ExtractLyricLinesFromLrc(cleaned);
        if (lines.Count <= 1)
            return false;

        string allText = string.Join(" ", lines);
        if (allText.Contains("暂无歌词") ||
            allText.Contains("纯音乐") ||
            allText.Contains("请欣赏"))
        {
            return false;
        }

        return true;
    }

    public async Task<string> FetchLyricsAsync(
        string keyword,
        List<string> biliTexts = null,
        double? targetDurationSeconds = null)
    {
        // ---------- 搜索歌曲 ----------
        List<SongInfo> songs = null;
        foreach (var source in _sources)
        {
            GD.Print($"[LyricsFetcher] 尝试搜索源: {source.GetType().Name}");
            songs = await source.SearchAsync(keyword);
            if (songs != null && songs.Count > 0)
            {
                GD.Print($"[LyricsFetcher] {source.GetType().Name} 返回 {songs.Count} 首候选");
                break;
            }
            GD.Print($"[LyricsFetcher] {source.GetType().Name} 无结果，尝试下一个源");
        }

        if (songs == null || songs.Count == 0)
        {
            GD.Print("[LyricsFetcher] 所有源搜索无结果");
            return null;
        }

        // ---------- 选择最佳匹配 ----------
        SongInfo bestSong;
        if (biliTexts != null && biliTexts.Count > 0)
        {
            string biliText = string.Join(" ", biliTexts);
            double bestScore = 0.15;
            bestSong = songs[0];
            foreach (var song in songs)
            {
                string fullText = $"{song.Name} {song.Artist}";
                double score = SubtitleUtils.CalculateSimilarity(biliText, fullText);
                GD.Print($"[LyricsFetcher] 候选: {song.Name} - {song.Artist} 相似度={score:F4}");
                if (score > bestScore)
                {
                    bestScore = score;
                    bestSong = song;
                }
            }
        }
        else
        {
            bestSong = songs[0];
        }
        GD.Print($"[LyricsFetcher] 选定歌曲: ID={bestSong.Id}, {bestSong.Name} - {bestSong.Artist}");

        // ---------- 获取歌词 ----------
        foreach (var source in _sources)
        {
            string sourceName = source.GetType().Name;
            GD.Print($"[LyricsFetcher] 尝试从 {sourceName} 获取歌词...");
            string lrc = await source.GetLyricAsync(bestSong);

            // 记录尝试
            UpdateStat(sourceName, attempt: true, success: false);

            if (!string.IsNullOrEmpty(lrc) && IsLyricContentValid(lrc))
            {
                // 记录成功
                UpdateStat(sourceName, attempt: false, success: true);
                GD.Print($"[LyricsFetcher] 从 {sourceName} 获取成功，歌词有效");
                WriteStatsToLog();
                return lrc;
            }
            else
            {
                GD.Print($"[LyricsFetcher] {sourceName} 返回无效歌词，尝试下一个源");
            }
        }

        GD.Print("[LyricsFetcher] 所有源获取歌词均失败或返回无效内容");
        WriteStatsToLog();
        return null;
    }

    // ==================== 统计更新与持久化 ====================

    private static void UpdateStat(string sourceName, bool attempt, bool success)
    {
        lock (_statsLock)
        {
            if (!_sourceStats.ContainsKey(sourceName))
                _sourceStats[sourceName] = (0, 0, 0f);

            var (a, s, acc) = _sourceStats[sourceName];
            if (attempt) a++;
            if (success) s++;
            acc = a > 0 ? (float)s / a * 100.0f : 0.0f;
            _sourceStats[sourceName] = (a, s, acc);

            // 每次更新立即持久化，防止程序崩溃丢失数据
            SaveStatsToFile();
        }
    }

    private static void SaveStatsToFile()
    {
        try
        {
            // 将 C# 字典转换为 Godot 字典以供 Json.Stringify 使用
            var godotDict = new Godot.Collections.Dictionary();
            foreach (var kvp in _sourceStats)
            {
                var inner = new Godot.Collections.Dictionary
                {
                    ["attempts"] = kvp.Value.attempts,
                    ["successes"] = kvp.Value.successes
                };
                godotDict[kvp.Key] = inner;
            }

            string json = Json.Stringify(godotDict);
            string absolutePath = ProjectSettings.GlobalizePath(StatsJsonPath);
            File.WriteAllText(absolutePath, json);
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"[LyricsFetcher] 保存统计 JSON 失败: {e.Message}");
        }
    }

    private static void LoadStatsFromFile()
    {
        string absolutePath = ProjectSettings.GlobalizePath(StatsJsonPath);
        if (!File.Exists(absolutePath)) return;

        try
        {
            string json = File.ReadAllText(absolutePath);
            var parseResult = Json.ParseString(json);

            // Godot 4: Json.ParseString 直接返回 Variant，成功时是 Dictionary
            if (parseResult.VariantType != Variant.Type.Dictionary)
            {
                GD.PrintErr("[LyricsFetcher] JSON 格式无效，不是字典");
                return;
            }

            var data = parseResult.AsGodotDictionary();
            lock (_statsLock)
            {
                _sourceStats.Clear();
                foreach (string key in data.Keys)
                {
                    var inner = data[key].AsGodotDictionary();
                    int attempts = (int)inner["attempts"].AsDouble();
                    int successes = (int)inner["successes"].AsDouble();
                    float accuracy = successes > 0 ? (float)successes / attempts * 100.0f : 0.0f;
                    _sourceStats[key] = (attempts, successes, accuracy);
                }
            }
            GD.Print($"[LyricsFetcher] 已加载历史统计：{string.Join(", ", _sourceStats.Select(kvp => $"{kvp.Key}({kvp.Value.attempts}/{kvp.Value.successes})"))}");
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"[LyricsFetcher] 加载统计 JSON 失败: {e.Message}");
        }
    }

    // ==================== 可读日志追加 ====================

    private static void WriteStatsToLog()
    {
        string path = "user://lyrics_fetcher_stats.log";
        try
        {
            // 先确保文件存在且带有表头
            if (!Godot.FileAccess.FileExists(path))
            {
                using var createFile = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
                if (createFile != null)
                {
                    createFile.StoreLine("===== 歌词源统计记录 =====");
                    createFile.StoreLine($"开始记录时间：{System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    createFile.StoreLine(new string('-', 60));
                }
                else
                {
                    GD.PrintErr("[LyricsFetcher] 无法创建日志文件");
                    return;
                }
            }

            // 以读写模式打开并直接定位到末尾追加内容
            using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.ReadWrite);
            if (file == null)
            {
                GD.PrintErr("[LyricsFetcher] 无法打开日志文件");
                return;
            }

            file.SeekEnd(0);
            file.StoreLine($"\n统计时间：{System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            file.StoreLine(new string('-', 40));

            lock (_statsLock)
            {
                if (_sourceStats.Count == 0)
                {
                    file.StoreLine("本次无统计数据");
                }
                else
                {
                    foreach (var kvp in _sourceStats.OrderByDescending(x => x.Value.successes))
                    {
                        string name = kvp.Key;
                        int att = kvp.Value.attempts;
                        int succ = kvp.Value.successes;
                        double rate = att > 0 ? (double)succ / att * 100.0 : 0.0;
                        file.StoreLine($"{name}: 尝试 {att} 次, 成功 {succ} 次, 成功率 {rate:F1}%");
                    }
                }
            }
            file.StoreLine(new string('=', 60));

            GD.Print($"[LyricsFetcher] 统计日志已追加写入");
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"[LyricsFetcher] 写入日志文件异常: {e.Message}");
        }
    }
}