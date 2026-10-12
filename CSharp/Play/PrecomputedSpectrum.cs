using System;
using System.Collections.Generic;
using System.IO;

public sealed class SpectrumData
{
    internal const uint FileMagic = 0x43505347;

    public double StartTimeSeconds;
    public double FramesPerSecond;
    public int FineBandCount;
    public float MinHz = 20f;
    public float MaxHz = 22050f;

    private readonly object _sync = new();
    private readonly List<float[]> _frames = new();

    public int FrameCount { get { lock (_sync) return _frames.Count; } }

    internal void Configure(double startTime, double fps, int bandCount, float minHz, float maxHz)
    {
        StartTimeSeconds = startTime;
        FramesPerSecond = fps;
        FineBandCount = bandCount;
        MinHz = minHz;
        MaxHz = maxHz;
    }

    internal void ClearFrames() { lock (_sync) _frames.Clear(); }

    internal void AddFrame(float[] energies) { lock (_sync) _frames.Add(energies); }

    private int BandIndex(float hz)
    {
        if (hz <= MinHz) return 0;
        if (hz >= MaxHz) return FineBandCount - 1;
        double ratio = Math.Log(hz / MinHz) / Math.Log(MaxHz / MinHz);
        int idx = (int)(ratio * FineBandCount);
        return idx < 0 ? 0 : (idx >= FineBandCount ? FineBandCount - 1 : idx);
    }

    public float GetBandEnergy(float minHz, float maxHz, double timeSeconds)
    {
        if (FineBandCount <= 0 || FramesPerSecond <= 0) return 0f;
        if (maxHz <= MinHz || minHz >= MaxHz || minHz >= maxHz) return 0f;

        int idx = (int)Math.Round((timeSeconds - StartTimeSeconds) * FramesPerSecond);
        float[] frame;
        lock (_sync)
        {
            if (idx < 0 || idx >= _frames.Count) return 0f;
            frame = _frames[idx];
        }

        int lo = BandIndex(minHz < MinHz ? MinHz : minHz);
        int hi = BandIndex(maxHz > MaxHz ? MaxHz : maxHz);
        float best = 0f;
        for (int i = lo; i <= hi; i++)
            if (frame[i] > best) best = frame[i];
        return best;
    }

    public void Save(string path)
    {
        lock (_sync)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var w = new BinaryWriter(fs);
            w.Write(FileMagic);
            w.Write(1);
            w.Write(StartTimeSeconds);
            w.Write(FramesPerSecond);
            w.Write(FineBandCount);
            w.Write(MinHz);
            w.Write(MaxHz);
            w.Write(_frames.Count);
            foreach (var fr in _frames)
                for (int i = 0; i < FineBandCount; i++)
                    w.Write(fr[i]);
        }
    }

    public static SpectrumData Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var r = new BinaryReader(fs);
        if (r.ReadUInt32() != FileMagic)
            throw new InvalidDataException("频谱缓存文件格式不正确");
        _ = r.ReadInt32();
        var d = new SpectrumData
        {
            StartTimeSeconds = r.ReadDouble(),
            FramesPerSecond = r.ReadDouble(),
            FineBandCount = r.ReadInt32(),
            MinHz = r.ReadSingle(),
            MaxHz = r.ReadSingle()
        };
        int count = r.ReadInt32();
        for (int f = 0; f < count; f++)
        {
            var fr = new float[d.FineBandCount];
            for (int i = 0; i < d.FineBandCount; i++)
                fr[i] = r.ReadSingle();
            d._frames.Add(fr);
        }
        return d;
    }
}

public sealed class SpectrumPreprocessor
{
    public const int SampleRate = 44100;
    private const int BytesPerFrame = 4;

    private readonly int _fftSize;
    private readonly int _hop;
    private readonly int _bandCount;
    private readonly float _minHz;
    private readonly float _maxHz;

    private readonly float[] _hann;
    private readonly int[] _binBand;
    private readonly float _powerScale;

    private readonly float[] _ringL, _ringR;
    private readonly float[] _reL, _imL, _reR, _imR;
    private readonly float[] _powL, _powR;
    private long _samplesReceived;
    private long _nextFrameIndex;

    private readonly object _feedLock = new();

    public SpectrumData Data { get; } = new();

    public SpectrumPreprocessor(int fftSize = 4096, int hop = 512,
                                int fineBandCount = 256,
                                float minHz = 20f, float maxHz = 22050f)
    {
        if (fftSize < 16 || (fftSize & (fftSize - 1)) != 0)
            throw new ArgumentException("fftSize 必须为 2 的幂且 >= 16");
        if (hop < 1 || hop > fftSize)
            throw new ArgumentException("hop 取值 1..fftSize");
        if (fineBandCount < 8)
            throw new ArgumentException("fineBandCount >= 8");
        if (minHz <= 0f || maxHz <= minHz)
            throw new ArgumentException("频带范围参数不合法");

        _fftSize = fftSize;
        _hop = hop;
        _bandCount = fineBandCount;
        _minHz = minHz;
        _maxHz = Math.Min(maxHz, SampleRate / 2f);

        int half = fftSize / 2;
        _hann = new float[fftSize];
        for (int i = 0; i < fftSize; i++)
            _hann[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (fftSize - 1)));

        _binBand = new int[half + 1];
        double logRatio = Math.Log(_maxHz / _minHz);
        for (int bin = 1; bin <= half; bin++)
        {
            float f = (float)bin * SampleRate / fftSize;
            int band = (int)(Math.Log(f / _minHz) / logRatio * _bandCount);
            _binBand[bin] = band < 0 ? 0 : (band >= _bandCount ? _bandCount - 1 : band);
        }

        _powerScale = 4f / (fftSize * (float)fftSize);

        _ringL = new float[fftSize];
        _ringR = new float[fftSize];
        _reL = new float[fftSize]; _imL = new float[fftSize];
        _reR = new float[fftSize]; _imR = new float[fftSize];
        _powL = new float[_bandCount];
        _powR = new float[_bandCount];

        Data.Configure(0, SampleRate / (double)_hop, _bandCount, _minHz, _maxHz);
    }

    public void Reset(double startTimeSeconds)
    {
        lock (_feedLock)
        {
            Data.Configure(startTimeSeconds, SampleRate / (double)_hop, _bandCount, _minHz, _maxHz);
            Data.ClearFrames();
            _samplesReceived = 0;
            _nextFrameIndex = 0;
            Array.Clear(_ringL, 0, _fftSize);
            Array.Clear(_ringR, 0, _fftSize);
        }
    }

    public void FeedPcm(byte[] chunk)
    {
        if (chunk == null || chunk.Length < BytesPerFrame) return;
        int sampleFrames = chunk.Length / BytesPerFrame;

        lock (_feedLock)
        {
            for (int f = 0; f < sampleFrames; f++)
            {
                int o = f * BytesPerFrame;
                float l = (short)(chunk[o] | (chunk[o + 1] << 8)) / 32768f;
                float r = (short)(chunk[o + 2] | (chunk[o + 3] << 8)) / 32768f;
                int pos = (int)(_samplesReceived % _fftSize);
                _ringL[pos] = l;
                _ringR[pos] = r;
                _samplesReceived++;

                while (_nextFrameIndex * _hop + _fftSize <= _samplesReceived)
                {
                    ProcessFrame(_nextFrameIndex);
                    _nextFrameIndex++;
                }
            }
        }
    }

    private void ProcessFrame(long frameIndex)
    {
        long startSample = frameIndex * _hop;
        for (int j = 0; j < _fftSize; j++)
        {
            float w = _hann[j];
            int idx = (int)((startSample + j) % _fftSize);
            _reL[j] = _ringL[idx] * w; _imL[j] = 0f;
            _reR[j] = _ringR[idx] * w; _imR[j] = 0f;
        }

        Fft(_reL, _imL);
        Fft(_reR, _imR);

        Array.Clear(_powL, 0, _bandCount);
        Array.Clear(_powR, 0, _bandCount);
        int half = _fftSize / 2;
        for (int bin = 1; bin <= half; bin++)
        {
            int band = _binBand[bin];
            float pl = _reL[bin] * _reL[bin] + _imL[bin] * _imL[bin];
            if (pl > _powL[band]) _powL[band] = pl;
            float pr = _reR[bin] * _reR[bin] + _imR[bin] * _imR[bin];
            if (pr > _powR[band]) _powR[band] = pr;
        }

        float[] energies = new float[_bandCount];
        for (int b = 0; b < _bandCount; b++)
            energies[b] = (_powL[b] + _powR[b]) * _powerScale;
        Data.AddFrame(energies);
    }

    private static void Fft(float[] re, float[] im)
    {
        int n = re.Length;

        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len >> 1;
            (float[] cosT, float[] sinT) = GetTwiddles(len);
            for (int blk = 0; blk < n; blk += len)
            {
                for (int j = 0; j < half; j++)
                {
                    int a = blk + j, b = a + half;
                    float vr = re[b] * cosT[j] - im[b] * sinT[j];
                    float vi = re[b] * sinT[j] + im[b] * cosT[j];
                    re[b] = re[a] - vr;
                    im[b] = im[a] - vi;
                    re[a] += vr;
                    im[a] += vi;
                }
            }
        }
    }

    private static readonly Dictionary<int, float[]> _twCos = new();
    private static readonly Dictionary<int, float[]> _twSin = new();
    private static readonly object _twLock = new();

    private static (float[] cos, float[] sin) GetTwiddles(int len)
    {
        lock (_twLock)
        {
            if (!_twCos.TryGetValue(len, out var cos))
            {
                int half = len >> 1;
                cos = new float[half];
                var sin = new float[half];
                for (int j = 0; j < half; j++)
                {
                    double ang = 2.0 * Math.PI * j / len;
                    cos[j] = (float)Math.Cos(ang);
                    sin[j] = (float)Math.Sin(ang);
                }
                _twCos[len] = cos;
                _twSin[len] = sin;
                return (cos, sin);
            }
            return (cos, _twSin[len]);
        }
    }
}