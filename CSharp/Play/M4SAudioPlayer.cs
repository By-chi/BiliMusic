using Godot;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public partial class M4SAudioPlayer : Node
{
    #region 音频参数
    private const int BytesPerFrame = 4;
    private const int MinBufferFrames = 44100 * 10;
    private const int FramesPerBlock = 2048;
    private static readonly int MinBufferBlocks = (MinBufferFrames + FramesPerBlock - 1) / FramesPerBlock;
    private const int SeekMinBufferBlocks = 10;
    #endregion

    #region 播放器组件
    private AudioStreamPlayer _audioPlayer;
    private AudioStreamGeneratorPlayback _playback;
    private readonly ConcurrentQueue<byte[]> _pcmQueue = new();
    #endregion

    #region 状态标志
    private bool _isPlaying;
    private bool _isStopped;
    private bool _decodingCompleted;
    private bool _isPaused;
    private double _currentAudioDuration;
    private bool _isLoading = false;
    private readonly SemaphoreSlim _playLock = new(1, 1);
    #endregion

    #region 预缓冲控制
    private bool _bufferReady;
    private int _requiredBufferBlocks = MinBufferBlocks; // 动态缓冲需求
    private byte[] _currentChunk;
    private int _currentChunkOffset;
    private Vector2[] _buffer = new Vector2[FramesPerBlock];
    #endregion

    #region 临时文件与取消支持
    private string _tempFilePath;
    private CancellationTokenSource _cts;
    private Task _currentPlayTask;
    public string CurrentAudioFilePath;
    #endregion

    #region 模拟位置
    private double _simulatedPosition;
    #endregion

    #region 信号
    [Signal]
    public delegate void PlaybackErrorEventHandler(string error);
    [Signal]
    public delegate void FinishEventHandler();
    private bool _finishedEmitted;
    #endregion

    public void SetAudioPlayer(AudioStreamPlayer player)
    {
        _audioPlayer = player ?? throw new ArgumentNullException(nameof(player));
        var generator = new AudioStreamGenerator { MixRate = 44100, BufferLength = 2f };
        _audioPlayer.Stream = generator;
    }
    public override void _Ready()
	{
        if ((bool)GetNode("/root/GdScriptFunc").Call("get_data", "Options", "Enable_HigherProcessPriority", true)){
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
                    GD.Print("[CSharpFunc] 进程优先级已设置为 High");
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[CSharpFunc] 设置进程优先级失败: {ex.Message}");
                }
            }
            else
            {
                GD.Print($"[CSharpFunc] 当前平台: {(OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "Unknown")}，进程优先级设置功能不支持");
            }
        }
	}
    public override void _Process(double delta)
    {
        if (_audioPlayer == null || _playback == null || _isStopped)
            return;

        if (!_bufferReady)
        {
            if (_pcmQueue.Count >= _requiredBufferBlocks)
            {
                _bufferReady = true;
                GD.Print($"预缓冲完成，队列中有 {_pcmQueue.Count} 个数据块，要求最少 {_requiredBufferBlocks}");
            }
            return;
        }

        if (_isPaused)
            return;

        if (_isPlaying)
        {
            _simulatedPosition += delta;
            if (_simulatedPosition > _currentAudioDuration && _currentAudioDuration > 0)
                _simulatedPosition = _currentAudioDuration;
        }

        int framesAvailable = _playback.GetFramesAvailable();
        if (framesAvailable == 0)
            return;

        while (framesAvailable > 0)
        {
            if (_currentChunk == null || _currentChunkOffset >= _currentChunk.Length)
            {
                if (!_pcmQueue.TryDequeue(out _currentChunk))
                {
                    if (_decodingCompleted && _pcmQueue.IsEmpty)
                    {
                        if (!_finishedEmitted)
                        {
                            _finishedEmitted = true;
                            _simulatedPosition = _currentAudioDuration;
                            GD.Print("播放结束");
                            EmitSignal(SignalName.Finish);
                            StopPlayback();
                        }
                    }
                    else
                    {
                        Array.Clear(_buffer, 0, FramesPerBlock);
                        int silentFrames = Math.Min(FramesPerBlock, framesAvailable);
                        _playback?.PushBuffer(_buffer.AsSpan(0, silentFrames));
                    }
                    break;
                }
                _currentChunkOffset = 0;
            }

            int framesToPush = Math.Min(FramesPerBlock, framesAvailable);
            int bytesToPush = framesToPush * BytesPerFrame;
            int bytesRemaining = _currentChunk.Length - _currentChunkOffset;
            int bytesToTake = Math.Min(bytesToPush, bytesRemaining);
            int framesToTake = bytesToTake / BytesPerFrame;

            if (framesToTake == 0)
                break;

            for (int i = 0; i < framesToTake; i++)
            {
                int offset = _currentChunkOffset + i * BytesPerFrame;
                short left = (short)(_currentChunk[offset] | (_currentChunk[offset + 1] << 8));
                short right = (short)(_currentChunk[offset + 2] | (_currentChunk[offset + 3] << 8));
                _buffer[i] = new Vector2(left / 32768.0f, right / 32768.0f);
            }

            _playback.PushBuffer(_buffer.AsSpan(0, framesToTake));
            _currentChunkOffset += bytesToTake;
            framesAvailable -= framesToTake;
        }
    }

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
            _audioPlayer.StreamPaused = false;
            _isPaused = false;
            GD.Print("播放已恢复");
        }
    }

    public async Task PlayAsync(string url, string referer = null)
    {
        if (_audioPlayer == null) throw new InvalidOperationException("AudioStreamPlayer 未设置");

        Task newPlayTask;
        await _playLock.WaitAsync();
        try
        {
            // 取消并等待旧任务
            var oldTask = _currentPlayTask;
            _currentPlayTask = null;
            if (oldTask != null && !oldTask.IsCompleted)
            {
                _cts?.Cancel();
                try { await oldTask; } catch (OperationCanceledException) { }
            }

            if (!string.IsNullOrEmpty(_tempFilePath) && File.Exists(_tempFilePath))
            {
                try { File.Delete(_tempFilePath); } catch { }
            }

            _tempFilePath = Path.GetTempFileName();
            CurrentAudioFilePath = _tempFilePath;

            StopPlayback();

            _isPlaying = true;
            _isStopped = false;
            _decodingCompleted = false;
            _bufferReady = false;
            _requiredBufferBlocks = MinBufferBlocks;
            _isPaused = false;
            _pcmQueue.Clear();
            _simulatedPosition = 0;
            _finishedEmitted = false;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            newPlayTask = PlayInternalAsync(url, referer, token);
            _currentPlayTask = newPlayTask;
        }
        finally
        {
            _playLock.Release();
        }

        await newPlayTask;
    }

    private async Task PlayInternalAsync(string url, string referer, CancellationToken token)
    {
        _isLoading = true;

        if (_playback == null)
        {
            _audioPlayer.Play();
            _playback = (AudioStreamGeneratorPlayback)_audioPlayer.GetStreamPlayback();
            if (_playback == null)
            {
                _isLoading = false;
                throw new Exception("无法获取 AudioStreamGeneratorPlayback");
            }
        }

        using var fileStream = File.Open(_tempFilePath, FileMode.Create, System.IO.FileAccess.Write, FileShare.Read);
        var ffmpegProcess = AudioConverter.StartFFmpegPipe();

        var downloadTask = Task.Run(async () =>
        {
            try
            {
                using var multiStream = new MultiWriteStream(ffmpegProcess.StandardInput.BaseStream, fileStream);
                await DownloadAudio.StreamAudioToStreamAsync(url, referer, multiStream, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _isLoading = false;
                GD.PrintErr($"下载/保存失败: {ex.Message}");
            }
        }, token);

        var pcmReadTask = Task.Factory.StartNew(async () =>
        {
            try
            {
                using var outputStream = ffmpegProcess.StandardOutput.BaseStream;
                byte[] buffer = new byte[4096];
                int bytesRead;
                while ((bytesRead = await outputStream.ReadAsync(buffer, token)) > 0)
                {
                    int aligned = bytesRead / BytesPerFrame * BytesPerFrame;
                    if (aligned == 0) continue;
                    byte[] chunk = new byte[aligned];
                    Array.Copy(buffer, 0, chunk, 0, aligned);
                    _pcmQueue.Enqueue(chunk);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _isLoading = false;
                GD.PrintErr($"读取 PCM 数据失败: {ex.Message}");
            }
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        await downloadTask;
        ffmpegProcess.StandardInput.Close();
        await pcmReadTask;
        await ffmpegProcess.WaitForExitAsync(token);
        await fileStream.FlushAsync(token);
        fileStream.Close();
        _decodingCompleted = true;

        if (File.Exists(_tempFilePath))
        {
            double duration = await AudioConverter.GetAudioDurationAsync(_tempFilePath, token);
            if (duration > 0)
            {
                _currentAudioDuration = duration;
                GD.Print($"获取音频时长: {_currentAudioDuration} 秒");
            }
        }

        GD.Print("解码完成，等待播放队列清空");
        _isLoading = false;

        while (!_pcmQueue.IsEmpty && _isPlaying && !token.IsCancellationRequested)
        {
            await ToSignal(GetTree(), "process_frame");
        }
    }

    public async Task PlayLocalAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            EmitSignal(SignalName.PlaybackError, "文件路径无效或不存在");
            return;
        }
        if (_audioPlayer == null)
        {
            EmitSignal(SignalName.PlaybackError, "AudioStreamPlayer 未设置");
            return;
        }

        Task newPlayTask;
        await _playLock.WaitAsync();
        try
        {
            var oldTask = _currentPlayTask;
            _currentPlayTask = null;
            if (oldTask != null && !oldTask.IsCompleted)
            {
                _cts?.Cancel();
                try { await oldTask; } catch (OperationCanceledException) { }
                await Task.Delay(50);
            }

            StopPlayback();

            _isPlaying = true;
            _isStopped = false;
            _decodingCompleted = false;
            _bufferReady = false;
            _requiredBufferBlocks = MinBufferBlocks;
            _isPaused = false;
            _pcmQueue.Clear();
            _simulatedPosition = 0;
            _finishedEmitted = false;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            CurrentAudioFilePath = filePath;
            _tempFilePath = null;

            newPlayTask = PlayLocalInternalAsync(filePath, token);
            _currentPlayTask = newPlayTask;
        }
        finally
        {
            _playLock.Release();
        }

        await newPlayTask;
    }

    public void PlayLocal(string filePath) =>
        _ = PlayLocalAsync(filePath).ContinueWith(t =>
        {
            if (t.IsFaulted) GD.PrintErr($"本地播放失败：{t.Exception}");
        }, TaskScheduler.FromCurrentSynchronizationContext());

    private async Task PlayLocalInternalAsync(string filePath, CancellationToken token)
    {
        _isLoading = true;

        if (_playback == null)
        {
            _audioPlayer.Play();
            _playback = (AudioStreamGeneratorPlayback)_audioPlayer.GetStreamPlayback();
            if (_playback == null)
            {
                _isLoading = false;
                EmitSignal(SignalName.PlaybackError, "无法获取 AudioStreamGeneratorPlayback");
                return;
            }
        }

        try
        {
            double duration = await AudioConverter.GetAudioDurationAsync(filePath, token);
            if (duration > 0)
                _currentAudioDuration = duration;
            else
                GD.PrintErr("获取本地音频时长失败");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"获取本地音频时长失败: {ex.Message}");
        }

        var decodeTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in AudioConverter.DecodeAudioToPcm44100Async(filePath, 0, token))
                {
                    _pcmQueue.Enqueue(chunk);
                }
                _decodingCompleted = true;
                GD.Print("本地文件解码完成");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _isLoading = false;
                GD.PrintErr($"本地文件解码错误: {ex.Message}");
                EmitSignal(SignalName.PlaybackError, ex.Message);
            }
        }, token);

        _isLoading = false;
        await decodeTask;

        while (!_pcmQueue.IsEmpty && _isPlaying && !token.IsCancellationRequested)
        {
            await ToSignal(GetTree(), "process_frame");
        }
    }

    public void StopPlayback()
    {
        _isStopped = true;
        _isPlaying = false;
        _decodingCompleted = false;
        _isPaused = false;
        _bufferReady = false;
        _pcmQueue.Clear();
        _currentChunk = null;
        _currentChunkOffset = 0;
        _simulatedPosition = 0;
        _cts?.Cancel();
        _audioPlayer?.Stop();
        _audioPlayer.StreamPaused = false;
        _playback = null;
    }

    public double GetCurrentPosition() => _simulatedPosition;

    public float GetCurrentPercentage()
    {
        double duration = _currentAudioDuration;
        if (duration <= 0) return 0;
        return (float)Math.Clamp(GetCurrentPosition() / duration, 0, 1);
    }

    public float GetCurrentAudioDuration() => (float)_currentAudioDuration;

    public async Task SeekPercentageAsync(float percentage)
    {
        double duration = _currentAudioDuration;
        if (duration <= 0 && !string.IsNullOrEmpty(CurrentAudioFilePath) && File.Exists(CurrentAudioFilePath))
        {
            var token = _cts?.Token ?? CancellationToken.None;
            duration = await AudioConverter.GetAudioDurationAsync(CurrentAudioFilePath, token);
            _currentAudioDuration = duration;
        }
        double seconds = Math.Clamp(percentage, 0f, 1f) * duration;
        await SeekAsync(seconds);
    }

    public void SeekPercentage(float percentage) =>
        _ = SeekPercentageAsync(percentage).ContinueWith(t =>
        {
            if (t.IsFaulted) GD.PrintErr($"SeekPercentage 失败: {t.Exception}");
        }, TaskScheduler.FromCurrentSynchronizationContext());

    private async Task SeekAsync(double seconds)
    {
        if (_isLoading) return;

        Task seekTask;
        await _playLock.WaitAsync();
        try
        {
            if (string.IsNullOrEmpty(CurrentAudioFilePath) || !File.Exists(CurrentAudioFilePath))
            {
                GD.PrintErr("Seek 失败：没有有效的音频文件");
                EmitSignal(SignalName.PlaybackError, "无法 Seek，音频文件不存在");
                return;
            }

            var oldTask = _currentPlayTask;
            _currentPlayTask = null;
            if (oldTask != null && !oldTask.IsCompleted)
            {
                _cts?.Cancel();
                try { await oldTask; } catch (OperationCanceledException) { }
                await Task.Delay(50);
            }

            StopPlayback();

            _isPlaying = true;
            _isStopped = false;
            _decodingCompleted = false;
            _bufferReady = false;
            _requiredBufferBlocks = SeekMinBufferBlocks;
            _pcmQueue.Clear();
            _simulatedPosition = seconds;
            _finishedEmitted = false;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            double duration = _currentAudioDuration;
            if (duration <= 0)
            {
                duration = await AudioConverter.GetAudioDurationAsync(CurrentAudioFilePath, token);
                _currentAudioDuration = duration;
            }
            seconds = Math.Clamp(seconds, 0, duration);

            if (_playback == null)
            {
                _audioPlayer.Play();
                _playback = (AudioStreamGeneratorPlayback)_audioPlayer.GetStreamPlayback();
                if (_playback == null)
                {
                    GD.PrintErr("Seek 失败：无法启动播放器");
                    return;
                }
            }

            seekTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var chunk in AudioConverter.DecodeAudioToPcm44100Async(CurrentAudioFilePath, seconds, token))
                    {
                        _pcmQueue.Enqueue(chunk);
                    }
                    _decodingCompleted = true;
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    GD.PrintErr($"Seek 解码错误: {ex.Message}");
                    EmitSignal(SignalName.PlaybackError, ex.Message);
                    _isPlaying = false;
                }
            }, token);

            _currentPlayTask = seekTask;
        }
        finally
        {
            _playLock.Release();
        }

        await seekTask;
    }

    public void Seek(double seconds) =>
        _ = SeekAsync(seconds).ContinueWith(t =>
        {
            if (t.IsFaulted) GD.PrintErr($"Seek 失败: {t.Exception}");
        }, TaskScheduler.FromCurrentSynchronizationContext());

    public async Task PlayByIdentifierAsync(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            EmitSignal(SignalName.PlaybackError, "标识符不能为空");
            return;
        }

        try
        {
            string audioUrl, referer, title, coverUrl;
            if (identifier.StartsWith("BV", StringComparison.OrdinalIgnoreCase))
                (audioUrl, referer, title, coverUrl) = await DownloadAudio.Instance.GetAudioInfoByBvAsync(identifier);
            else if (identifier.StartsWith("au", StringComparison.OrdinalIgnoreCase))
                (audioUrl, referer, title, coverUrl) = await DownloadAudio.Instance.GetAudioInfoByAuIdAsync(identifier);
            else
                throw new ArgumentException("无法识别的标识符，请输入 BV 号或 AU 号。");

            await PlayAsync(audioUrl, referer);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"自动播放失败: {ex.Message}");
            EmitSignal(SignalName.PlaybackError, ex.Message);
        }
    }

    public void PlayByIdentifier(string identifier) =>
        _ = PlayByIdentifierAsync(identifier).ContinueWith(t =>
        {
            if (t.IsFaulted) GD.PrintErr($"播放失败：{t.Exception}");
        }, TaskScheduler.FromCurrentSynchronizationContext());

    private class MultiWriteStream(params Stream[] streams) : Stream
    {
        private readonly Stream[] _streams = streams;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
            foreach (var s in _streams) s.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            foreach (var s in _streams) s.Write(buffer, offset, count);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            foreach (var s in _streams)
                await s.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        }
    }
}