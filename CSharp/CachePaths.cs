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
            try
            {
                File.Delete(f);
            }
            catch (IOException)
            {
                // 文件正被播放器占用，跳过并在下次启动清理
                GD.Print($"临时音频文件被占用，跳过清理: {f}");
            }
            catch (UnauthorizedAccessException)
            {
                GD.Print($"临时音频文件无权限删除: {f}");
            }
        }
    }
}
