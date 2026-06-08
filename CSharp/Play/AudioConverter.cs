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

#if DEBUG
    private const string DebugFfmpegPath = @"D:\MSYS2\home\By.chi\ffmpeg-master\ffmpeg.exe";
    private const string DebugFfprobePath = @"D:\MSYS2\home\By.chi\ffmpeg-master\ffprobe.exe";
#endif

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

    public static string FfmpegPath
    {
        get
        {
#if DEBUG
            return DebugFfmpegPath;
#else
            return Path.Combine(FfmpegBasePath, GetExecutableName("ffmpeg"));
#endif
        }
    }

    public static string FfprobePath
    {
        get
        {
#if DEBUG
            return DebugFfprobePath;
#else
            return Path.Combine(FfmpegBasePath, GetExecutableName("ffprobe"));
#endif
        }
    }

    public static bool CheckFFmpegAvailable()
    {
        return File.Exists(FfmpegPath) && File.Exists(FfprobePath);
    }

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

        // 异步读取 stderr，防止阻塞
        _ = Task.Run(() => ReadErrorAsync(process, CancellationToken.None));
        return process;
    }

    private static async Task ReadErrorAsync(Process process, CancellationToken token)
    {
        try
        {
            string error = await process.StandardError.ReadToEndAsync();
            if (!string.IsNullOrEmpty(error))
                GD.PrintErr($"FFmpeg 错误: {error}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GD.PrintErr($"读取 FFmpeg 错误流异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 异步解码音频文件，产出 44.1kHz 16bit 立体声 PCM 块
    /// </summary>
    /// <param name="filePath">音频文件路径</param>
    /// <param name="startSeconds">开始时间（秒）</param>
    /// <param name="cancellationToken">取消令牌</param>
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

        // 异步消费 stderr
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

    /// <summary>
    /// 获取音频时长（秒），失败返回 0
    /// </summary>
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

        // 异步读取输出和错误
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