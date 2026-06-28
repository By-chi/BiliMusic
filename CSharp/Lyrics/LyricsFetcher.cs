using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HttpClient = System.Net.Http.HttpClient;
public class LyricsFetcher
{
    private readonly List<ILyricsSource> _sources;

    public LyricsFetcher(HttpClient http, params ILyricsSource[] sources)
    {
        _sources = sources?.ToList() ?? new List<ILyricsSource>();
    }

    public async Task<string> FetchLyricsAsync(
        string keyword,
        List<string> biliTexts = null,
        double? targetDurationSeconds = null)
    {
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

        foreach (var source in _sources)
        {
            GD.Print($"[LyricsFetcher] 尝试从 {source.GetType().Name} 获取歌词...");
            string lrc = await source.GetLyricAsync(bestSong);
            if (!string.IsNullOrEmpty(lrc))
            {
                GD.Print($"[LyricsFetcher] 从 {source.GetType().Name} 获取成功");
                return lrc;
            }
            GD.Print($"[LyricsFetcher] {source.GetType().Name} 未返回有效歌词");
        }

        GD.Print("[LyricsFetcher] 所有源获取歌词均失败");
        return null;
    }
}