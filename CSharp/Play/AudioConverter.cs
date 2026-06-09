using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Godot;

public partial class AudioConverter
{
    private const int FramesPerBlock = 1024;
    private const int BytesPerFrame = 4;
    private const int PcmBlockSize = FramesPerBlock * BytesPerFrame;
    private static string GetDebugFfmpegPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return @"D:\MSYS2\home\By.chi\ffmpeg-master\ffmpeg.exe";
        }
        else if (OperatingSystem.IsMacOS())
        {
            // macOS 用户需要在 Homebrew 安装 ffmpeg: brew install ffmpeg
            return "/usr/local/bin/ffmpeg";
        }
        else if (OperatingSystem.IsLinux())
        {
            // Linux 用户需要在系统包管理器安装 ffmpeg
            return "/usr/bin/ffmpeg";
        }
        throw new NotSupportedException($"不支持的操作系统");
    }

    private static string GetDebugFfprobePath()
    {
        if (OperatingSystem.IsWindows())
        {
            return @"D:\MSYS2\home\By.chi\ffmpeg-master\ffprobe.exe";
        }
        else if (OperatingSystem.IsMacOS())
        {
            return "/usr/local/bin/ffprobe";
        }
        else if (OperatingSystem.IsLinux())
        {
            return "/usr/bin/ffprobe";
        }
        throw new NotSupportedException($"不支持的操作系统");
    }

    /// <summary>
    /// macOS：执行 shell 命令（which / brew）
    /// </summary>
    private static string RunBashCommand(string command)
    {
        try
        {
            var process = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{command}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var p = Process.Start(process);
            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return output;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// macOS：自动安装 ffmpeg
    /// </summary>
    private static void InstallFFmpegOnMac()
    {
        GD.Print("macOS 未检测到 ffmpeg，尝试自动安装：brew install ffmpeg");
        RunBashCommand("brew install ffmpeg");

        // 安装后再次检测
        string ffmpeg = RunBashCommand("which ffmpeg");
        string ffprobe = RunBashCommand("which ffprobe");

        if (!string.IsNullOrEmpty(ffmpeg) && !string.IsNullOrEmpty(ffprobe))
        {
            GD.Print("✅ ffmpeg 自动安装成功");
        }
        else
        {
            GD.PrintErr("❌ 自动安装失败，请手动安装：brew install ffmpeg");
        }
    }

    // ==================== 路径获取（核心修改） ====================
    public static string FfmpegPath
    {
        get
        {
#if DEBUG
            // DEBUG模式下Windows用本地路径，其他系统走系统ffmpeg
            if (OperatingSystem.IsWindows())
                return DebugFfmpegPath;
#endif
            // Windows：使用本地打包的 ffmpeg
            if (OperatingSystem.IsWindows())
                return Path.Combine(FfmpegBasePath, GetExecutableName("ffmpeg"));

            // macOS：优先使用系统 ffmpeg（which 查找）
            if (OperatingSystem.IsMacOS())
            {
                string systemPath = RunBashCommand("which ffmpeg");
                if (!string.IsNullOrEmpty(systemPath))
                    return systemPath;

                // 找不到 → 自动安装
                InstallFFmpegOnMac();

                // 安装后再查一次
                systemPath = RunBashCommand("which ffmpeg");
                if (!string.IsNullOrEmpty(systemPath))
                    return systemPath;
            }

            // 兜底：使用本地 ffmpeg
            return Path.Combine(FfmpegBasePath, GetExecutableName("ffmpeg"));
        }
    }

    public static string FfprobePath
    {
        get
        {
#if DEBUG
            // DEBUG模式下Windows用本地路径，其他系统走系统ffprobe
            if (OperatingSystem.IsWindows())
                return DebugFfprobePath;
#endif
            if (OperatingSystem.IsWindows())
                return Path.Combine(FfmpegBasePath, GetExecutableName("ffprobe"));

            if (OperatingSystem.IsMacOS())
            {
                string systemPath = RunBashCommand("which ffprobe");
                if (!string.IsNullOrEmpty(systemPath))
                    return systemPath;

                // 安装后再查
                systemPath = RunBashCommand("which ffprobe");
                if (!string.IsNullOrEmpty(systemPath))
                    return systemPath;
            }

            return Path.Combine(FfmpegBasePath, GetExecutableName("ffprobe"));
        }
    }

    // ==================== 原有基础方法 ====================
    public static string FfmpegBasePath
    {
        get
        {
            string exeDir = Path.GetDirectoryName(OS.GetExecutablePath());
            return Path.Combine(exeDir, "ffmpeg");
        }
    }

    private static string GetExecutableName(string baseName)
    {
        return OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;
    }

    public static bool CheckFFmpegAvailable()
    {
        try
        {
            bool ffmpegExists = File.Exists(FfmpegPath);
            bool ffprobeExists = File.Exists(FfprobePath);

            if (!ffmpegExists || !ffprobeExists)
            {
                // macOS 额外提示
                if (OperatingSystem.IsMacOS())
                {
                    GD.PrintErr($"FFmpeg 缺失！请安装：brew install ffmpeg");
                    GD.PrintErr($"ffmpeg 路径：{FfmpegPath}");
                    GD.PrintErr($"ffprobe 路径：{FfprobePath}");
                }
            }

            return ffmpegExists && ffprobeExists;
        }
        catch
        {
            return false;
        }
    }

    // ==================== 以下为原有逻辑，完全不变 ====================
    public static Process StartFFmpegPipe()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            Arguments = "-i pipe:0 -f s16le -acodec pcm_s16le -ar 44100 -ac 2 -",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var process = new Process { StartInfo = startInfo };
        process.Start();

        _ = Task.Run(() => ReadErrorAsync(process, CancellationToken.None));
        return process;
    }

    private static async Task ReadErrorAsync(Process process, CancellationToken token)
    {
        try
        {
            string error = await process.StandardError.ReadToEndAsync();
            // 过滤ffmpeg正常日志，只打印真正的错误
            if (!string.IsNullOrEmpty(error) && 
                (error.Contains("error", StringComparison.OrdinalIgnoreCase) || 
                 error.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                 error.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                 error.Contains("could not", StringComparison.OrdinalIgnoreCase) ||
                 error.Contains("cannot", StringComparison.OrdinalIgnoreCase)))
                GD.PrintErr($"FFmpeg 错误: {error}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GD.PrintErr($"读取 FFmpeg 错误流异常: {ex.Message}");
        }
    }

    public static async IAsyncEnumerable<byte[]> DecodeAudioToPcm44100Async(
        string filePath,
        double startSeconds,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var args = new StringBuilder();
        args.Append($"-i \"{filePath}\" ");
        if (startSeconds > 0)
            args.Append($"-ss {startSeconds} ");
        args.Append("-f s16le -acodec pcm_s16le -ar 44100 -ac 2 -");

        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            Arguments = args.ToString(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stderrTask = Task.Run(() => ReadErrorAsync(process, cancellationToken), cancellationToken);

        using var outputStream = process.StandardOutput.BaseStream;
        byte[] buffer = new byte[PcmBlockSize];
        int bytesRead;

        try
        {
            while ((bytesRead = await outputStream.ReadAsync(buffer.AsMemory(0, PcmBlockSize), cancellationToken)) > 0)
            {
                int alignedBytes = bytesRead / BytesPerFrame * BytesPerFrame;
                if (alignedBytes == 0) continue;

                byte[] chunk = new byte[alignedBytes];
                Array.Copy(buffer, 0, chunk, 0, alignedBytes);
                yield return chunk;
            }

            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                GD.PrintErr($"FFmpeg 解码退出码异常：{process.ExitCode}");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(true); }
                catch { }
            }
            await stderrTask;
        }
    }

    public static async Task<double> GetAudioDurationAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var args = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"";
        var startInfo = new ProcessStartInfo
        {
            FileName = FfprobePath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        string output = await outputTask;
        string error = await errorTask;

        if (process.ExitCode != 0)
        {
            GD.PrintErr($"ffprobe 错误: {error}");
            return 0;
        }

        return double.TryParse(output.Trim(), out double duration) ? duration : 0;
    }

    [Obsolete("此方法使用 NAudio，建议改用 FFmpeg 实现")]
    public static AudioStream LoadAudioStream(string path) { throw new NotImplementedException(); }

    [Obsolete("此方法使用 NAudio，建议改用 FFmpeg 实现")]
    public static async Task<bool> ConvertM4sToMp3Async(string inputPath, string outputPath) { throw new NotImplementedException(); }
}