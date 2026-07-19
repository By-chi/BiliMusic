using Godot;

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
    private AudioStreamPlayer playerNode;
    private AudioEffectSpectrumAnalyzerInstance analyzer;
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
        // 获取第 1 个总线的第 0 个效果（需确保总线上有 SpectrumAnalyzer 效果）
        var instance = AudioServer.GetBusEffectInstance(1, 0);
        analyzer = instance as AudioEffectSpectrumAnalyzerInstance;
        playerNode=GetNode<AudioStreamPlayer>("/root/Player");
        maxFrequency = AudioServer.GetMixRate() / 2.0f;
        ampScale = spectrumScale;

        smoothedBandEnergies = new float[lineCount];
        for (int i = 0; i < lineCount; i++)
            smoothedBandEnergies[i] = 0.0f;

        currentMax = toHz;
        targetMax = toHz;
    }

    /// <summary>
    /// 每帧调用，返回 lineCount 条频谱的高度值（已平滑、加权、粘连，并 clamp 到 [10, maxHeight]）
    /// </summary>
    public float[] Update(float delta, bool isPlaying, float maxHeight)
    {
        ComputeAlpha(delta);
        AutoAdjustAmpScale(isPlaying);
        UpdateDynamicRange(delta);
        float[] heights = ComputeRawHeights(maxHeight);

        if (enableNeighborBlend)
            heights = ApplyNeighborBlend(heights);

        return heights;
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
        if (playerNode.VolumeLinear <= 0.0f)
            return 0.0f;
        Vector2 mag = analyzer.GetMagnitudeForFrequencyRange(minFreq, maxFreq)/playerNode.VolumeLinear;
        return mag.X * mag.X + mag.Y * mag.Y;
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
            rawHeights[i] = Mathf.Clamp(scaled, 10.0f, maxHeight);
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