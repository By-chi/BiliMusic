using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FileAccess = System.IO.FileAccess;
using Thread = System.Threading.Thread;

/// <summary>
/// M4S 音频播放器：网络流边下载边解码（ffmpeg 管道 → PCM 队列 → AudioStreamGenerator），
/// 支持 DASH sidx 索引的远程定点 Seek。Seek/起播采用“挂起播放器直到预缓冲完成”策略。
/// </summary>
public partial class M4SAudioPlayer : Node
{
    #region 常量
    private const int MixRate = 44100;
    private const int BytesPerFrame = 4;              // s16le 立体声
    private const int FramesPerBlock = 2048;
    private const double PrebufferSeconds = 3;        // 起播预缓冲
    private const double SeekPrebufferSeconds = 1;    // Seek 预缓冲
    private static int BlocksFor(double seconds) => (int)Math.Ceiling(MixRate * seconds / FramesPerBlock);
    private static readonly int MinBufferBlocks = BlocksFor(PrebufferSeconds);
    private static readonly int SeekMinBufferBlocks = BlocksFor(SeekPrebufferSeconds);
    private static readonly int MaxQueueBlocks = BlocksFor(60);   // PCM 队列上限（背压控制）
    private const int ReadBufferSize = 16 * 1024;
    private const int ProbeMaxAttempts = 60;          // 元数据探测上限（约 30 秒）
    #endregion

    #region 状态
    private AudioStreamPlayer _audioPlayer;
    private AudioStreamGeneratorPlayback _playback;
    private readonly ConcurrentQueue<byte[]> _pcmQueue = new();
    private bool _isPlaying, _isPaused, _decodingCompleted, _isStreamingRemote,
        _isFullyDownloaded, _isLoading, _bufferReady, _prebufferHold, _finishedEmitted;
    private int _requiredBufferBlocks;
    private double _currentAudioDuration, _simulatedPosition;   // Generator 无内置位置，手动模拟
    private readonly Vector2[] _buffer = new Vector2[FramesPerBlock];
    private byte[] _currentChunk;
    private int _currentChunkOffset;
    private readonly SemaphoreSlim _playLock = new(1, 1);
    private CancellationTokenSource _cts;
    private Task _currentPlayTask;
    public string CurrentAudioFilePath;

    // 网络流定点 Seek
    private string _tempFilePath, _currentUrl, _currentReferer;
    private byte[] _initSegment;                       // ftyp+moov(+sidx)，定点续传时需先写入
    private long _totalFileSize;
    private List<(double StartSec, double DurSec, long FileOffset)> _segmentIndex;
    #endregion

    #region 信号
    [Signal]
    public delegate void PlaybackErrorEventHandler(string error);
    [Signal]
    public delegate void FinishEventHandler();

    /// <summary>跨线程安全的错误信号发送（后台线程 EmitSignal 不安全）</summary>
    private void EmitPlaybackError(string message) =>
        CallDeferred(MethodName.EmitPlaybackErrorDeferred, message);

    public void EmitPlaybackErrorDeferred(string message) =>
        EmitSignal(SignalName.PlaybackError, message);
    #endregion

    public void SetAudioPlayer(AudioStreamPlayer player)
    {
        _audioPlayer = player ?? throw new ArgumentNullException(nameof(player));
        _audioPlayer.Stream = new AudioStreamGenerator { MixRate = MixRate, BufferLength = 2f };
    }

    public override void _Ready()
    {
        CachePaths.CleanTempAudio();
        if (OperatingSystem.IsWindows()
            && GetFuncNode() is Node funcNode
            && funcNode.Call("get_data", "Options", "Enable_HigherProcessPriority", true).AsBool())
        {
            try
            {
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
                GD.Print("[CSharpFunc] 进程优先级已设置为 High");
            }
            catch (Exception ex) { GD.PrintErr($"[CSharpFunc] 设置进程优先级失败: {ex.Message}"); }
        }
    }

    /// <summary>原代码节点名写法不一致，两种路径都尝试</summary>
    private Node GetFuncNode() =>
        GetNodeOrNull("/root/GdScriptFunc") ?? GetNodeOrNull("/root/GDScriptFunc");

    public override void _Process(double delta)
    {
        if (!_isPlaying || _playback == null)
            return;

        // 预缓冲：数据足够前播放器保持挂起（不输出静音，位置不推进）
        if (!_bufferReady)
        {
            if (_pcmQueue.Count < _requiredBufferBlocks)
                return;
            _bufferReady = true;
            _isLoading = false;
            GD.Print($"预缓冲完成：{_pcmQueue.Count}/{_requiredBufferBlocks} 块");
        }

        if (!_isPaused)
        {
            _simulatedPosition += delta;
            if (_currentAudioDuration > 0 && _simulatedPosition > _currentAudioDuration)
                _simulatedPosition = _currentAudioDuration;
        }
        // 暂停时仍继续向下填充数据，保证 Resume 无缝

        int framesAvailable = _playback.GetFramesAvailable();
        while (framesAvailable > 0)
        {
            if (_currentChunk == null || _currentChunkOffset >= _currentChunk.Length)
            {
                if (!_pcmQueue.TryDequeue(out _currentChunk))
                {
                    // 断供：空 buffer 由 Godot 补静音；解码全部结束则触发 Finish
                    // （TryDequeue 失败时 out 被置 null）
                    if (!_isPaused && _decodingCompleted && !_finishedEmitted)
                    {
                        _finishedEmitted = true;
                        _simulatedPosition = _currentAudioDuration;
                        GD.Print("播放结束");
                        EmitSignal(SignalName.Finish);
                        StopPlayback();
                    }
                    break;
                }
                _currentChunkOffset = 0;
            }

            int bytesToTake = Math.Min(Math.Min(FramesPerBlock, framesAvailable) * BytesPerFrame,
                _currentChunk.Length - _currentChunkOffset);
            int framesToTake = bytesToTake / BytesPerFrame;
            if (framesToTake == 0)
                break;

            for (int i = 0; i < framesToTake; i++)
            {
                int offset = _currentChunkOffset + i * BytesPerFrame;
                _buffer[i] = new Vector2(
                    (short)(_currentChunk[offset] | (_currentChunk[offset + 1] << 8)) / 32768f,
                    (short)(_currentChunk[offset + 2] | (_currentChunk[offset + 3] << 8)) / 32768f);
            }

            _playback.PushBuffer(_buffer.AsSpan(0, framesToTake));
            _currentChunkOffset += bytesToTake;
            framesAvailable -= framesToTake;
        }

        // 数据已填入缓冲，解除挂起——声音无缝从起播/Seek 点开始
        if (_prebufferHold)
        {
            _prebufferHold = false;
            if (!_isPaused)
                _audioPlayer.StreamPaused = false;
        }
    }

    #region 公共控制
    public void Pause()
    {
        if (_isPlaying && !_isPaused)
        {
            _isPaused = true;
            _audioPlayer.StreamPaused = true;
            GD.Print("播放已暂停");
        }
    }

    public void Resume()
    {
        if (_isPlaying && _isPaused)
        {
            _isPaused = false;
            _audioPlayer.StreamPaused = _prebufferHold;   // 预缓冲未完成时继续保持挂起
            GD.Print("播放已恢复");
        }
    }

    public void StopPlayback()
    {
        _isPlaying = false;
        _decodingCompleted = false;
        _isPaused = false;
        _bufferReady = false;
        _isLoading = false;
        _prebufferHold = false;
        _pcmQueue.Clear();
        _currentChunk = null;
        _currentChunkOffset = 0;
        try { _cts?.Cancel(); } catch { }
        if (_audioPlayer != null)
        {
            _audioPlayer.Stop();
            _audioPlayer.StreamPaused = false;
        }
        _playback = null;
    }

    public Task PlayAsync(string url, string referer = null) =>
        StartSessionAsync(() =>
        {
            RotateTempFile();
            ResetSession(MinBufferBlocks, 0);
            _currentAudioDuration = 0;
            _isStreamingRemote = true;
            _isFullyDownloaded = false;
            _currentUrl = url;
            _currentReferer = referer;
            _initSegment = null;
            _segmentIndex = null;
            _totalFileSize = 0;
        }, token => StreamPlayInternalAsync(url, referer, 0, token));

    public async Task PlayLocalAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            EmitPlaybackError("文件路径无效或不存在");
            return;
        }
        if (_audioPlayer == null)
        {
            EmitPlaybackError("AudioStreamPlayer 未设置");
            return;
        }
        await StartSessionAsync(() =>
        {
            _tempFilePath = null;
            CurrentAudioFilePath = filePath;
            ResetSession(MinBufferBlocks, 0);
            _currentAudioDuration = 0;
            _isStreamingRemote = false;
            _isFullyDownloaded = true;
        }, token => PlayLocalInternalAsync(filePath, 0, token));
    }

    public async Task SeekAsync(double seconds)
    {
        // 预缓冲尚未完成时没有可靠的 seek 基准，忽略本次请求
        if (_isLoading)
        {
            GD.Print("[Seek] 忽略：预缓冲尚未完成");
            return;
        }

        Task seekTask = null;
        await _playLock.WaitAsync();
        try
        {
            if (string.IsNullOrEmpty(CurrentAudioFilePath) || !File.Exists(CurrentAudioFilePath))
            {
                EmitPlaybackError("无法 Seek，音频文件不存在");
                return;
            }

            await CancelCurrentTaskAsync();
            bool wasPaused = _isPaused;
            StopPlayback();
            var token = (_cts = new CancellationTokenSource()).Token;

            double duration = _currentAudioDuration;
            if (duration <= 0)
                _currentAudioDuration = duration =
                    await AudioConverter.GetAudioDurationAsync(CurrentAudioFilePath, token);
            if (duration > 0)
                seconds = Math.Clamp(seconds, 0, duration);

            bool canLocal = !_isStreamingRemote || _isFullyDownloaded;
            long restartOffset = -1;
            if (!canLocal)
            {
                restartOffset = MapSecondsToByteOffset(seconds, duration);
                if (restartOffset < 0)
                {
                    EmitPlaybackError("正在解析音频索引，请稍后再试");
                    return;
                }
            }

            ResetSession(SeekMinBufferBlocks, seconds);

            try { EnsurePlayback(); }
            catch (Exception ex)
            {
                GD.PrintErr($"Seek 失败：{ex.Message}");
                EmitPlaybackError(ex.Message);
                _isPlaying = false;
                _isLoading = false;
                return;
            }

            if (canLocal)
            {
                _isStreamingRemote = false;
                seekTask = PlayLocalInternalAsync(CurrentAudioFilePath, seconds, token);
            }
            else
            {
                GD.Print($"[Seek] 定点续传：{seconds:F1}s → 字节偏移 {restartOffset}");
                RotateTempFile();
                _isStreamingRemote = true;
                _isFullyDownloaded = false;
                seekTask = StreamPlayInternalAsync(_currentUrl, _currentReferer, restartOffset, token);
            }

            _currentPlayTask = seekTask;
            if (wasPaused)
                Pause();
        }
        finally { _playLock.Release(); }

        if (seekTask != null)
            await seekTask;
    }

    public async Task SeekPercentageAsync(float percentage)
    {
        double duration = _currentAudioDuration;
        if (duration <= 0 && !string.IsNullOrEmpty(CurrentAudioFilePath) && File.Exists(CurrentAudioFilePath))
        {
            duration = await AudioConverter.GetAudioDurationAsync(CurrentAudioFilePath, _cts?.Token ?? CancellationToken.None);
            _currentAudioDuration = duration;
        }
        await SeekAsync(Math.Clamp(percentage, 0f, 1f) * duration);
    }

    public void PlayByIdentifier(string identifier) => _ = RunFireAndForget(PlayByIdentifierAsync(identifier), "播放");
    public void PlayLocal(string filePath) => _ = RunFireAndForget(PlayLocalAsync(filePath), "本地播放");
    public void Seek(double seconds) => _ = RunFireAndForget(SeekAsync(seconds), "Seek");
    public void SeekPercentage(float percentage) => _ = RunFireAndForget(SeekPercentageAsync(percentage), "SeekPercentage");

    private static async Task RunFireAndForget(Task task, string op)
    {
        try { await task; }
        catch (Exception ex) { GD.PrintErr($"{op}失败: {ex.Message}"); }
    }
    #endregion

    #region 查询
    public double GetCurrentPosition() => _simulatedPosition;

    public float GetCurrentPercentage() => _currentAudioDuration <= 0
        ? 0
        : (float)Math.Clamp(GetCurrentPosition() / _currentAudioDuration, 0, 1);

    public float GetCurrentAudioDuration() => (float)_currentAudioDuration;
    #endregion

    #region 会话管理（私有）
    /// <summary>统一会话启动：取消旧任务 → Stop → 初始化 → 锁外执行新任务</summary>
    private async Task StartSessionAsync(Action setup, Func<CancellationToken, Task> run)
    {
        Task task = null;
        if (_audioPlayer == null)
            throw new InvalidOperationException("AudioStreamPlayer 未设置");
        await _playLock.WaitAsync();
        try
        {
            await CancelCurrentTaskAsync();
            StopPlayback();
            setup();
            _cts = new CancellationTokenSource();
            task = _currentPlayTask = run(_cts.Token);
        }
        finally { _playLock.Release(); }
        if (task != null)
            await task;
    }

    /// <summary>删除旧临时文件并生成新路径（起播 / 远程 Seek 共用）</summary>
    private void RotateTempFile()
    {
        try { if (!string.IsNullOrEmpty(_tempFilePath) && File.Exists(_tempFilePath)) File.Delete(_tempFilePath); } catch { }
        _tempFilePath = CachePaths.NewTempAudioPath(".m4s");
        CurrentAudioFilePath = _tempFilePath;
    }

    /// <summary>取消并等待旧的播放任务退出（含给 ffmpeg 退出留出的缓冲时间）</summary>
    private async Task CancelCurrentTaskAsync()
    {
        var old = _currentPlayTask;
        _currentPlayTask = null;
        if (old == null || old.IsCompleted)
            return;
        try { _cts?.Cancel(); } catch { }
        try { await old; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { GD.PrintErr($"[M4S] 旧任务异常退出: {ex.Message}"); }
        await Task.Delay(50);
    }

    /// <summary>开启新的播放会话（调用前必须先 StopPlayback）</summary>
    private void ResetSession(int requiredBufferBlocks, double startPosition)
    {
        _isPlaying = true;
        _isPaused = false;
        _decodingCompleted = false;
        _bufferReady = false;
        _isLoading = true;
        _requiredBufferBlocks = requiredBufferBlocks;
        _finishedEmitted = false;
        _simulatedPosition = startPosition;
    }

    /// <summary>获取 playback 并挂起播放器等待预缓冲（起播/Seek 后不立即出声，避免静音卡顿）</summary>
    private void EnsurePlayback()
    {
        if (_playback != null)
            return;
        _audioPlayer.Play();
        _playback = (AudioStreamGeneratorPlayback)_audioPlayer.GetStreamPlayback();
        if (_playback == null)
        {
            _audioPlayer.Stop();
            throw new InvalidOperationException("无法获取 AudioStreamGeneratorPlayback");
        }
        _audioPlayer.StreamPaused = true;
        _prebufferHold = true;
    }

    /// <summary>等待 _Process 消费完队列（轮询，避免从线程池线程调用 Godot API）</summary>
    private async Task WaitForQueueDrainAsync(CancellationToken token)
    {
        while (!_pcmQueue.IsEmpty && _isPlaying && !token.IsCancellationRequested)
            await Task.Delay(50, token);
    }
    #endregion

    #region 内部播放任务
    private async Task StreamPlayInternalAsync(string url, string referer, long startOffset, CancellationToken token)
    {
        Process ffmpeg = null;
        FileStream fileStream = null;
        CancellationTokenRegistration killReg = default;
        try
        {
            EnsurePlayback();

            fileStream = File.Open(_tempFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            ffmpeg = AudioConverter.StartFFmpegPipe();
            killReg = token.Register(() =>
            {
                try { if (ffmpeg is { HasExited: false }) ffmpeg.Kill(entireProcessTree: true); } catch { }
            });

            StartMetadataProbe(token);

            var downloadTask = Task.Run(async () =>
            {
                try
                {
                    using var multiStream = new MultiWriteStream(ffmpeg.StandardInput.BaseStream, fileStream);
                    if (startOffset > 0)
                        await multiStream.WriteAsync(_initSegment, 0, _initSegment.Length, token);

                    // 比例估算的偏移可能落在片段中间，需丢弃数据直到下一个 moof 边界
                    Stream dest = (startOffset > 0 && _segmentIndex == null)
                        ? new SyncSkipStream(multiStream)
                        : multiStream;

                    await DownloadAudio.StreamAudioToStreamAsync(url, referer, dest, token, startOffset,
                        total => _totalFileSize = total);
                    _isFullyDownloaded = true;
                    GD.Print("[下载] 音频已完整落盘");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    // 取消旧任务时流被释放属正常现象，不向用户报错
                    if (!token.IsCancellationRequested)
                    {
                        GD.PrintErr($"下载失败: {ex.Message}");
                        EmitPlaybackError("下载失败: " + ex.Message);
                    }
                }
            }, token);

            var pcmReadTask = Task.Run(async () =>
            {
                try
                {
                    var output = ffmpeg.StandardOutput.BaseStream;
                    byte[] buf = new byte[ReadBufferSize];
                    int read;
                    while ((read = await output.ReadAsync(buf, token)) > 0)
                    {
                        int aligned = read / BytesPerFrame * BytesPerFrame;
                        if (aligned == 0) continue;
                        byte[] chunk = new byte[aligned];
                        Array.Copy(buf, 0, chunk, 0, aligned);
                        EnqueuePcm(chunk, token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                        GD.PrintErr($"读取 PCM 失败: {ex.Message}");
                }
            }, token);

            await downloadTask.WaitAsync(token);

            // 关闭 ffmpeg 输入使其排空解码数据并正常退出；缺少这一步会死等输入导致挂起
            try { ffmpeg.StandardInput.Close(); } catch { }

            await pcmReadTask;
            await ffmpeg.WaitForExitAsync(CancellationToken.None);

            _decodingCompleted = true;
            if (_currentAudioDuration <= 0)
            {
                double duration = await AudioConverter.GetAudioDurationAsync(_tempFilePath, token);
                if (duration > 0)
                {
                    _currentAudioDuration = duration;
                    GD.Print($"[M4S] 音频时长: {duration:F1}s");
                }
            }

            GD.Print("解码完成，等待播放队列清空");
            await WaitForQueueDrainAsync(token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GD.PrintErr($"播放失败: {ex.Message}");
            EmitPlaybackError(ex.Message);
        }
        finally
        {
            if (!_bufferReady)
                _isLoading = false;
            killReg.Dispose();
            try { if (ffmpeg is { HasExited: false }) ffmpeg.Kill(entireProcessTree: true); } catch { }
            ffmpeg?.Dispose();
            fileStream?.Dispose();
        }
    }

    /// <summary>本地播放（含 Seek 定点：startOffset > 0 即为本地 Seek 路径）</summary>
    private async Task PlayLocalInternalAsync(string filePath, double startOffset, CancellationToken token)
    {
        try
        {
            EnsurePlayback();

            if (_currentAudioDuration <= 0)
            {
                try
                {
                    double duration = await AudioConverter.GetAudioDurationAsync(filePath, token);
                    if (duration > 0) _currentAudioDuration = duration;
                    else GD.PrintErr("获取本地音频时长失败");
                }
                catch (Exception ex) { GD.PrintErr($"获取本地音频时长失败: {ex.Message}"); }
            }

            await Task.Run(async () =>
            {
                try
                {
                    await foreach (var chunk in AudioConverter.DecodeAudioToPcm44100Async(filePath, startOffset, token))
                        EnqueuePcm(chunk, token);
                    _decodingCompleted = true;
                    GD.Print("本地文件解码完成");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    GD.PrintErr($"本地解码错误: {ex.Message}");
                    EmitPlaybackError(ex.Message);
                    _isPlaying = false;
                }
            }, token);

            await WaitForQueueDrainAsync(token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GD.PrintErr($"本地播放失败: {ex.Message}");
            EmitPlaybackError(ex.Message);
        }
        finally
        {
            if (!_bufferReady)
                _isLoading = false;
        }
    }

    /// <summary>后台提前探测音频时长与 DASH 索引（供远程 Seek 使用）。
    /// sidx 一出现即可精确计算 fMP4 总时长，无需等 ffprobe（其常需下载较完整文件才能算出）。</summary>
    private void StartMetadataProbe(CancellationToken token) => _ = Task.Run(async () =>
    {
        try
        {
            for (int attempt = 0; attempt < ProbeMaxAttempts && !token.IsCancellationRequested; attempt++)
            {
                if (_currentAudioDuration > 0 && _initSegment != null)
                    break;
                await Task.Delay(500, token);
                if (!File.Exists(_tempFilePath) || new FileInfo(_tempFilePath).Length < 256 * 1024)
                    continue;

                if (_initSegment == null)
                {
                    TryCaptureDashIndex();
                    if (_segmentIndex is { Count: > 0 })
                    {
                        _currentAudioDuration = _segmentIndex[^1].StartSec + _segmentIndex[^1].DurSec;
                        GD.Print($"[探测] sidx 时长: {_currentAudioDuration:F1}s");
                    }
                }
                if (_currentAudioDuration > 0)
                    continue;

                double d = await AudioConverter.GetAudioDurationAsync(_tempFilePath, token);
                if (d > 0)
                {
                    _currentAudioDuration = d;
                    GD.Print($"[探测] 音频时长: {d:F1}s");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    });
    #endregion

    #region 标识符播放（BV / AU）
    public async Task PlayByIdentifierAsync(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            EmitPlaybackError("标识符不能为空");
            return;
        }
        try
        {
            string audioUrl, referer;
            if (identifier.StartsWith("BV", StringComparison.OrdinalIgnoreCase))
            {
                // 优先用登录态（SESSDATA）换取高音质地址，无登录则走免登录接口
                var v = GetFuncNode()?.Call("get_data", "AccountData", "SESSDATA", "");
                string sessdata = v?.VariantType == Variant.Type.String ? v.Value.AsString() : "";

                if (string.IsNullOrEmpty(sessdata))
                {
                    var info = await Task.Run(() => DownloadAudio.GetAudioInfoByBvSync(identifier));
                    if (info == null || info.Count == 0)
                        throw new Exception("未获取到音频信息");
                    audioUrl = info["audioUrl"].AsString();
                    referer = info["referer"].AsString();
                }
                else
                {
                    var basic = await DownloadAudio.Instance.GetVideoBasicInfoAsync(identifier);
                    referer = DownloadAudio.BuildVideoPageUrl(identifier);
                    audioUrl = await GetAudioUrlViaGDScript(identifier, basic.cid);
                }
            }
            else if (identifier.StartsWith("au", StringComparison.OrdinalIgnoreCase))
            {
                (audioUrl, referer, _, _) = await DownloadAudio.Instance.GetAudioInfoByAuIdAsync(identifier);
            }
            else
            {
                throw new ArgumentException("无法识别的标识符，请输入 BV 号或 AU 号。");
            }

            if (string.IsNullOrWhiteSpace(audioUrl))
                throw new Exception("未获取到音频地址");

            await PlayAsync(audioUrl, referer);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"自动播放失败: {ex.Message}");
            EmitPlaybackError(ex.Message);
        }
    }

    private Task<string> GetAudioUrlViaGDScript(string bvid, long cid)
    {
        var tcs = new TaskCompletionSource<string>();
        var api = GetNodeOrNull("/root/BilibiliApi");
        if (api == null)
        {
            tcs.TrySetResult("");
            return tcs.Task;
        }
        var callback = Callable.From((string url) => tcs.TrySetResult(url ?? ""));
        api.Call("fetch_audio_url", bvid, cid, callback);
        return tcs.Task;
    }
    #endregion

    #region PCM 队列
    /// <summary>入队（队列满时阻塞解码线程，形成对 ffmpeg/下载的背压）</summary>
    private void EnqueuePcm(byte[] chunk, CancellationToken token)
    {
        while (_pcmQueue.Count >= MaxQueueBlocks)
        {
            token.ThrowIfCancellationRequested();
            Thread.Sleep(2);
        }
        _pcmQueue.Enqueue(chunk);
    }
    #endregion

    #region DASH 解析
    private void TryCaptureDashIndex()
    {
        if (_initSegment != null || string.IsNullOrEmpty(_tempFilePath) || !File.Exists(_tempFilePath))
            return;
        try
        {
            using var fs = new FileStream(_tempFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 256 * 1024) return;
            byte[] head = new byte[(int)Math.Min(fs.Length, 8 * 1024 * 1024)];
            for (int read = 0; read < head.Length;)
            {
                int n = fs.Read(head, read, head.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (TryParseDashIndex(head, out var init, out var index))
            {
                _initSegment = init;
                _segmentIndex = index;
                GD.Print($"[DASH] 初始化段 {init.Length} 字节，片段索引 {index?.Count ?? 0} 段"
                    + (index != null
                        ? $"（覆盖至 {index[^1].StartSec + index[^1].DurSec:F0}s）"
                        : "（无 sidx，按比例估算 seek 偏移）"));
            }
        }
        catch { }
    }

    private static bool TryParseDashIndex(byte[] head, out byte[] initSegment,
        out List<(double StartSec, double DurSec, long FileOffset)> index)
    {
        initSegment = null;
        index = null;

        // 扫描顶层 box，首个 moof/mdat 之前即初始化段，同时记录 sidx 位置
        int pos = 0, sidxBody = -1, sidxBodyLen = 0;
        while (pos + 8 <= head.Length)
        {
            uint size = BE32(head, pos);
            if (size < 8 || pos + size > head.Length) break;
            uint type = BE32(head, pos + 4);
            if (type == 0x6D6F6F66 /*moof*/ || type == 0x6D646174 /*mdat*/)
            {
                initSegment = head[..pos];
                break;
            }
            if (type == 0x73696478 /*sidx*/) { sidxBody = pos + 8; sidxBodyLen = (int)size - 8; }
            pos += (int)size;
        }
        if (initSegment == null) return false;

        long anchor = pos;
        if (sidxBody < 0) return true;   // 无 sidx，仅初始化段

        try
        {
            int p = sidxBody;
            int version = head[p];
            p += 8;                                       // version/flags + reference_ID
            uint timescale = BE32(head, p); p += 4;
            if (timescale == 0) return true;
            ulong ept = version == 1 ? BE64(head, p) : BE32(head, p); p += version == 1 ? 8 : 4;
            ulong firstOff = version == 1 ? BE64(head, p) : BE32(head, p); p += version == 1 ? 8 : 4;
            p += 2;                                       // reserved
            ushort refCount = (ushort)(head[p] << 8 | head[p + 1]); p += 2;

            index = new List<(double, double, long)>(refCount);
            double t = ept / (double)timescale;
            long off = anchor + (long)firstOff;
            for (int i = 0; i < refCount && p + 12 <= sidxBody + sidxBodyLen; i++)
            {
                uint entry = BE32(head, p); p += 4;
                uint dur = BE32(head, p); p += 8;         // subsegment_duration + SAP 字段
                index.Add((t, dur / (double)timescale, off));
                t += dur / (double)timescale;
                off += entry & 0x7FFFFFFF;
            }
            if (index.Count == 0) index = null;
        }
        catch { index = null; }
        return true;
    }

    private long MapSecondsToByteOffset(double seconds, double duration)
    {
        if (_segmentIndex is { Count: > 0 })
        {
            foreach (var s in _segmentIndex)
                if (seconds < s.StartSec + s.DurSec)
                    return s.FileOffset;
            return _segmentIndex[^1].FileOffset;
        }
        if (duration <= 0 || _totalFileSize <= 0 || _initSegment == null) return -1;
        long mediaStart = _initSegment.Length;
        long mediaLen = Math.Max(1, _totalFileSize - mediaStart);
        return mediaStart + (long)(Math.Clamp(seconds / duration, 0, 0.999) * mediaLen);
    }

    private static uint BE32(byte[] b, int p) =>
        (uint)((b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]);

    private static ulong BE64(byte[] b, int p) =>
        ((ulong)BE32(b, p) << 32) | BE32(b, p + 4);
    #endregion

    #region 辅助流
    /// <summary>同时写入 ffmpeg stdin 与本地文件（Dispose 不关闭内部流，由调用方管理）</summary>
    private class MultiWriteStream(params Stream[] streams) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
            foreach (var s in streams) s.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            foreach (var s in streams) s.Write(buffer, offset, count);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            foreach (var s in streams)
                await s.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        }
    }

    /// <summary>丢弃数据直到 moof 边界（比例估算偏移的定点续传）。
    /// 用 8 字节滑动窗口（size+type）对齐 box 起点，并校验 size 防误同步。</summary>
    private sealed class SyncSkipStream(Stream inner) : Stream
    {
        private bool _synced;
        private readonly byte[] _win = new byte[8];
        private int _winLen;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_synced) { inner.Write(buffer, offset, count); return; }
            FilterWrite(buffer, offset, count, inner.Write);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (_synced) { await inner.WriteAsync(buffer.AsMemory(offset, count), ct); return; }
            using var ms = new MemoryStream();
            FilterWrite(buffer, offset, count, ms.Write);
            if (ms.Length > 0)
                await inner.WriteAsync(ms.GetBuffer(), 0, (int)ms.Length, ct);
        }

        private void FilterWrite(byte[] buffer, int offset, int count, Action<byte[], int, int> sink)
        {
            int end = offset + count;
            for (int i = offset; i < end; i++)
            {
                if (_winLen < 8) _win[_winLen++] = buffer[i];
                else { Array.Copy(_win, 1, _win, 0, 7); _win[7] = buffer[i]; }

                if (_winLen == 8 && _win[4] == (byte)'m' && _win[5] == (byte)'o'
                    && _win[6] == (byte)'o' && _win[7] == (byte)'f')
                {
                    uint size = BE32(_win, 0);
                    if (size is >= 8 and < 0x10000000)   // 合理 box 大小才认为同步成功
                    {
                        _synced = true;
                        sink(_win, 0, 8);
                        if (i + 1 < end) sink(buffer, i + 1, end - i - 1);
                        return;
                    }
                }
            }
        }
    }
    #endregion
}
