using Godot;
using System;

public partial class AudioSpectrumCalculator : Node
{
    [Export] public int lineCount = 90;
    [Export] public float fromHz = 400;
    [Export] public float toHz = 2000;
    [Export] public bool enableNeighborBlend = true;
    [Export] public int blendRadius = 1;
    [Export] public float spectrumScale = 9000.0f;
    [Export] public float attackCoefficient = 0.15f;
    [Export] public float releaseCoefficient = 0.75f;
    [Export] public bool autoAdjustAmpScale = true;
    [Export] public float targetScaledEnergy = 1000.0f;
    [Export] public float ampScaleAdjustSpeed = 0.1f;
    [Export] public float volumeThreshold = 0.001f;
    [Export] public float minAmpScale = 300.0f;
    [Export] public float maxAmpScale = 2000000.0f;
    [Export] public bool enableAWeighting = true;
    [Export] public float aWeightHighFreqPole = 8000.0f;
    [Export] public bool dynamicRange = true;
    [Export] public float maxChangedSpeed = 3000.0f;
    [Export] public float dynamicUpdateInterval = 0.2f;
    [Export] public float dynamicThreshold = 3.0f;
    [Export] public float perceptualBlend = 0.35f;

    public SpectrumData PrecomputedSpectrum;
    public Func<double> PositionProvider;

    private M4SAudioPlayer _boundPlayer;
    private bool _currentlyPlaying;
    private float _bindRetryTimer = 0f;
    private float[] smoothedBandEnergies;
    private float smoothedOverallEnergy = 0.0f;
    private float ampScale;
    private float currentAlphaAttack;
    private float currentAlphaRelease;
    private float maxFrequency;
    private float currentMax;
    private float targetMax;
    private float dynamicUpdateTimer = 0.0f;

    public override void _Ready()
    {
        maxFrequency = AudioServer.GetMixRate() / 2.0f;
        ampScale = spectrumScale;

        smoothedBandEnergies = new float[lineCount];
        for (int i = 0; i < lineCount; i++)
            smoothedBandEnergies[i] = 0.0f;

        currentMax = toHz;
        targetMax = toHz;
    }

    public float[] Update(float delta, bool isPlaying, float maxHeight)
    {
        TryBind();

        _currentlyPlaying = isPlaying;
        ComputeAlpha(delta);
        AutoAdjustAmpScale(isPlaying);
        UpdateDynamicRange(delta);
        float[] heights = ComputeRawHeights(maxHeight);

        if (enableNeighborBlend)
            heights = ApplyNeighborBlend(heights);

        return heights;
    }

    public void Bind(M4SAudioPlayer player)
    {
        if (player == null) return;
        _boundPlayer = player;
        PrecomputedSpectrum = player.SpectrumData;
        PositionProvider = player.GetCurrentPosition;
        GD.Print("[频谱绑定成功]（手动）");
    }

    private void TryBind()
    {
        if (_boundPlayer != null)
            return;

        _bindRetryTimer += 0.016f;
        bool report = _bindRetryTimer > 2f;
        if (report) _bindRetryTimer = 0f;

        var player = FindPlayer(GetTree().Root);
        if (player == null)
        {
            if (report) GD.Print("[频谱绑定失败] 场景树中找不到 M4SAudioPlayer 节点");
            return;
        }

        _boundPlayer = player;
        PrecomputedSpectrum = player.SpectrumData;
        PositionProvider = player.GetCurrentPosition;
        GD.Print("[频谱绑定成功] " + player.GetPath());
    }

    private static M4SAudioPlayer FindPlayer(Node root)
    {
        if (root is M4SAudioPlayer p)
            return p;
        foreach (var child in root.GetChildren())
        {
            var found = FindPlayer(child);
            if (found != null)
                return found;
        }
        return null;
    }

    private void ComputeAlpha(float delta)
    {
        currentAlphaAttack = ComputeAlphaCoefficient(attackCoefficient, delta);
        currentAlphaRelease = ComputeAlphaCoefficient(releaseCoefficient, delta);
    }

    private static float ComputeAlphaCoefficient(float coeff, float delta)
    {
        if (coeff >= 1.0f) return 0.0f;
        if (coeff <= 0.0f) return 1.0f;
        float alphaRef = 1.0f - coeff;
        float exponent = delta * 60.0f;
        return 1.0f - Mathf.Pow(1.0f - alphaRef, exponent);
    }

    private float GetRawBandEnergy(float minFreq, float maxFreq)
    {
        if (!_currentlyPlaying || PrecomputedSpectrum == null || PositionProvider == null)
            return 0.0f;
        double t = PositionProvider();
        return PrecomputedSpectrum.GetBandEnergy(minFreq, maxFreq, t);
    }

    private void AutoAdjustAmpScale(bool isPlaying)
    {
        if (autoAdjustAmpScale && isPlaying)
        {
            float overallRaw = GetRawBandEnergy(0, maxFrequency);
            if (overallRaw > smoothedOverallEnergy)
                smoothedOverallEnergy = Mathf.Lerp(smoothedOverallEnergy, overallRaw, currentAlphaAttack);
            else
                smoothedOverallEnergy = Mathf.Lerp(smoothedOverallEnergy, overallRaw, currentAlphaRelease);

            if (smoothedOverallEnergy > volumeThreshold)
            {
                float targetAmp = targetScaledEnergy / smoothedOverallEnergy;
                ampScale = Mathf.Lerp(ampScale, targetAmp, ampScaleAdjustSpeed);
                ampScale = Mathf.Clamp(ampScale, minAmpScale, maxAmpScale);
            }
        }
        else
        {
            ampScale = spectrumScale;
        }
    }

    private void UpdateDynamicRange(float delta)
    {
        if (!dynamicRange)
        {
            currentMax = toHz;
            return;
        }

        dynamicUpdateTimer += delta;
        if (dynamicUpdateTimer >= dynamicUpdateInterval)
        {
            dynamicUpdateTimer = 0.0f;
            float foundFreq = -1.0f;
            float step = 200.0f;
            float searchHz = 22050.0f;
            while (searchHz >= 500)
            {
                float low = Mathf.Max(searchHz - step, 20.0f);
                float high = searchHz;
                float raw = GetRawBandEnergy(low, high);
                if (raw * ampScale > dynamicThreshold)
                {
                    foundFreq = searchHz;
                    break;
                }
                searchHz -= step;
            }

            if (foundFreq > 0)
                targetMax = Mathf.Clamp(foundFreq, 500.0f, 22050.0f);
        }

        if (currentMax < targetMax)
            currentMax += maxChangedSpeed * delta;
        else
            currentMax -= maxChangedSpeed * delta;
        currentMax = Mathf.Clamp(currentMax, 500.0f, 22050.0f);
    }

    private float[] ComputeRawHeights(float maxHeight)
    {
        float currentLow = 20.0f;
        float currentHigh = currentMax;
        float blockSize = (currentHigh - currentLow) / lineCount;
        float[] rawHeights = new float[lineCount];
        float safeMaxHeight = Mathf.Max(maxHeight, 10.0f);
        float spanDb = 10.0f * MathF.Log10(safeMaxHeight / 10.0f);
        if (spanDb <= 1.0f) spanDb = 1.0f;

        for (int i = 0; i < lineCount; i++)
        {
            float lowFreq = currentLow + i * blockSize;
            float highFreq = lowFreq + blockSize;

            float rawEnergy = GetRawBandEnergy(lowFreq, highFreq);
            float smoothed = smoothedBandEnergies[i];
            if (rawEnergy > smoothed)
                smoothed = Mathf.Lerp(smoothed, rawEnergy, currentAlphaAttack);
            else
                smoothed = Mathf.Lerp(smoothed, rawEnergy, currentAlphaRelease);
            smoothedBandEnergies[i] = smoothed;

            float energyWeight = 1.0f;
            if (enableAWeighting)
            {
                float centerFreq = (lowFreq + highFreq) * 0.5f;
                float ampGain = GetAWeighting(centerFreq);
                energyWeight = ampGain * ampGain;
            }

            float scaled = smoothed * ampScale * energyWeight;

            float linearH = Mathf.Clamp(scaled, 10.0f, maxHeight);
            float relDb = 10.0f * MathF.Log10(Mathf.Max(scaled, 1e-12f) / 10.0f);
            float norm = Mathf.Clamp(relDb / spanDb, 0.0f, 1.0f);
            float perceptualH = 10.0f + (safeMaxHeight - 10.0f) * norm;

            rawHeights[i] = Mathf.Lerp(linearH, perceptualH, perceptualBlend);
        }

        return rawHeights;
    }

    private float GetAWeighting(float freqHz)
    {
        float f2 = freqHz * freqHz;
        const float LOW_POLE = 20.6f;
        float num = aWeightHighFreqPole * aWeightHighFreqPole * f2 * f2;
        float den = (f2 + LOW_POLE * LOW_POLE) *
                    Mathf.Sqrt((f2 + 107.7f * 107.7f) * (f2 + 737.9f * 737.9f)) *
                    (f2 + aWeightHighFreqPole * aWeightHighFreqPole);
        if (den == 0.0f) return 0.0f;
        return num / den;
    }

    private float[] ApplyNeighborBlend(float[] rawHeights)
    {
        int n = rawHeights.Length;
        float[] blended = new float[n];
        for (int i = 0; i < n; i++)
        {
            float sum = 0.0f;
            int cnt = 0;
            for (int offset = -blendRadius; offset <= blendRadius; offset++)
            {
                int idx = (i + offset) % n;
                if (idx < 0) idx += n;
                sum += rawHeights[idx];
                cnt++;
            }
            blended[i] = sum / cnt;
        }
        return blended;
    }
}