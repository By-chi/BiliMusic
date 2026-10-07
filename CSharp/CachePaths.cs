using Godot;
using System;
using System.IO;
public partial class CachePaths : Node
{
    public const string CacheRoot = "user://cache/";
    public const string TempAudioDir = CacheRoot + "temp_audio/";

    public static string GlobalTempAudioDir =>
        ProjectSettings.GlobalizePath(TempAudioDir);
    public static string NewTempAudioPath(string ext = ".m4s")
    {
        var dir = GlobalTempAudioDir;
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"audio_{Guid.NewGuid()}{ext}");
    }
    public static void CleanTempAudio()
    {
        var dir = GlobalTempAudioDir;
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir))
        {
            try { File.Delete(f); } catch { /* 文件被占用则跳过 */ }
        }
    }
}
