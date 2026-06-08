using System.Collections.Generic;
using System.Threading.Tasks;

public interface ILyricsSource
{
    Task<List<SongInfo>> SearchAsync(string keyword);
    Task<string> GetLyricAsync(SongInfo song);
}

public class SongInfo
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Artist { get; set; }
}