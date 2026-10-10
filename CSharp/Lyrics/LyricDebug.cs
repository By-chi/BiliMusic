using System.IO;
using Godot;

/// <summary>
/// 歌词系统调试开关。
/// 启用方式：在 user:// 目录下创建空文件 lyrics_debug_enabled 即可（无需改代码重编译）。
/// </summary>
public static class LyricDebug
{
    private static bool? _enabled;
    public static bool Enabled
    {
        get
        {
            _enabled ??= Godot.FileAccess.FileExists("user://lyrics_debug_enabled");
            return _enabled.Value;
        }
    }

    public static System.Collections.Generic.List<string> CurrentTrace;

    public static void Log(string msg)
    {
        if (Enabled) GD.Print(msg);
    }

    public static void DumpTrace(string trackName)
    {
        if (!Enabled || CurrentTrace == null || CurrentTrace.Count == 0) return;
        try
        {
            string dir = ProjectSettings.GlobalizePath("user://lyrics_debug");
            Directory.CreateDirectory(dir);
            string safeName = string.Join("_", trackName.Split(Path.GetInvalidFileNameChars()));
            string path = Path.Combine(dir, $"{safeName}_{System.DateTime.Now:HHmmss}.tsv");
            File.WriteAllLines(path, CurrentTrace);
            GD.Print($"[LyricDebug] 对齐明细已写入: {path}");
        }
        catch (System.Exception e) { GD.PrintErr($"[LyricDebug] 写入失败: {e.Message}"); }
        finally { CurrentTrace = null; }
    }
}
