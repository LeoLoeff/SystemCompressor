namespace SystemCompressor.Dsp;

/// <summary>
/// Stereo-linked feed-forward compressor for film dialogue. No allocation or locking occurs
/// in Process. Call Prepare whenever sample rate or settings change, outside the real-time callback.
///
/// Film-specific parts: the level detector ignores content below ~120 Hz (so explosions and
/// sub-bass do not trigger pumping), the transfer curve has an adjustable soft knee, and a peaking EQ
/// (1-8 kHz, default 3 kHz) after the compressor lifts the speech presence band.
/// </summary>
public sealed class StereoCompressor
{
    private const int MaxChannels = 8;
    private const float DetectorHighPassHz = 120f;
    private const float SoftClipStart = 0.89f; // ~ -1 dBFS
    private const float PresenceQ = 0.9f;

    private float _attackCoefficient;
    private float _releaseCoefficient;
    private float _thresholdDb;
    private float _ratio;
    private float _kneeDb;
    private float _makeupTarget;
    private float _makeupCurrent = 1f;   // smoothed towards the target (10 ms), so turning the Gain knob never clicks
    private float _makeupSmooth;
    private float _gainDb;
    private bool _enabled;
    private bool _presenceActive;

    private float _hpCoefficient;
    // Two cascaded one-pole high-passes (12 dB/oct) for the level detector.
    private readonly float[] _hpInput = new float[MaxChannels];
    private readonly float[] _hpOutput = new float[MaxChannels];
    private readonly float[] _hp2Output = new float[MaxChannels];

    private float _b0, _b1, _b2, _a1, _a2;
    private readonly float[] _z1 = new float[MaxChannels];
    private readonly float[] _z2 = new float[MaxChannels];

    public void Prepare(double sampleRate, CompressorSettings settings)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        settings = settings.Validate();
        _enabled = settings.Enabled;
        _thresholdDb = settings.ThresholdDb;
        _ratio = settings.Ratio;
        _kneeDb = settings.KneeDb;
        _makeupTarget = DbToLinear(settings.MakeupGainDb);
        _makeupSmooth = TimeCoefficient(10f, sampleRate);
        _attackCoefficient = TimeCoefficient(settings.AttackMs, sampleRate);
        _releaseCoefficient = TimeCoefficient(settings.ReleaseMs, sampleRate);
        _hpCoefficient = 1f / (1f + 2f * MathF.PI * DetectorHighPassHz / (float)sampleRate);

        _presenceActive = settings.SpeechBoostDb > 0.05f;
        if (_presenceActive) DesignPeakingEq(sampleRate, settings.SpeechBoostDb, settings.SpeechFreqHz);
    }

    // Interleaved float PCM: L,R,L,R ...; mono is also supported.
    public void Process(Span<float> samples, int channels)
    {
        if (channels is < 1 or > MaxChannels) throw new ArgumentOutOfRangeException(nameof(channels));
        if (samples.Length % channels != 0) throw new ArgumentException("Incomplete PCM frame.", nameof(samples));
        if (!_enabled) return;

        for (var frame = 0; frame < samples.Length; frame += channels)
        {
            // Detector: high-passed peak across all channels (stereo-linked).
            var peak = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                var x = samples[frame + channel];
                if (!float.IsFinite(x)) samples[frame + channel] = x = 0f;   // NaN/Inf must not poison the filters
                var y1 = _hpCoefficient * (_hpOutput[channel] + x - _hpInput[channel]);
                var y2 = _hpCoefficient * (_hp2Output[channel] + y1 - _hpOutput[channel]);
                _hpInput[channel] = x;
                _hpOutput[channel] = y1;
                _hp2Output[channel] = y2;
                peak = Math.Max(peak, MathF.Abs(y2));
            }

            var inputDb = LinearToDb(Math.Max(peak, 0.0000001f));
            var targetGainDb = SoftKneeGainDb(inputDb);

            // More attenuation means attack; recovering gain means release.
            var coefficient = targetGainDb < _gainDb ? _attackCoefficient : _releaseCoefficient;
            _gainDb = targetGainDb + coefficient * (_gainDb - targetGainDb);
            _makeupCurrent = _makeupTarget + _makeupSmooth * (_makeupCurrent - _makeupTarget);
            var gain = DbToLinear(_gainDb) * _makeupCurrent;

            for (var channel = 0; channel < channels; channel++)
            {
                var value = samples[frame + channel] * gain;
                if (_presenceActive)
                {
                    // Transposed direct form II biquad.
                    var output = _b0 * value + _z1[channel];
                    _z1[channel] = _b1 * value - _a1 * output + _z2[channel];
                    _z2[channel] = _b2 * value - _a2 * output;
                    value = output;
                }
                samples[frame + channel] = SoftClip(value);
            }
        }
    }

    // Safety net: transients that pass before the attack has closed (makeup gain + EQ boost)
    // are rounded off smoothly below 0 dBFS instead of hard-clipping. Untouched below ~-1 dBFS.
    private static float SoftClip(float x)
    {
        var magnitude = MathF.Abs(x);
        if (magnitude <= SoftClipStart) return x;
        var range = 1f - SoftClipStart;
        var shaped = SoftClipStart + range * MathF.Tanh((magnitude - SoftClipStart) / range);
        return MathF.CopySign(shaped, x);
    }

    private float SoftKneeGainDb(float inputDb)
    {
        var over = inputDb - _thresholdDb;
        var slope = 1f / _ratio - 1f;
        // Knee width 0 = hard knee; otherwise the curve bends smoothly over _kneeDb around the threshold.
        if (2f * over < -_kneeDb) return 0f;
        if (_kneeDb > 0f && 2f * over <= _kneeDb)
        {
            var t = over + _kneeDb / 2f;
            return slope * t * t / (2f * _kneeDb);
        }
        return slope * over;
    }

    // RBJ cookbook peaking filter.
    private void DesignPeakingEq(double sampleRate, float gainDb, float frequencyHz)
    {
        var a = MathF.Pow(10f, gainDb / 40f);
        var w0 = 2f * MathF.PI * frequencyHz / (float)sampleRate;
        var alpha = MathF.Sin(w0) / (2f * PresenceQ);
        var cos = MathF.Cos(w0);
        var a0 = 1f + alpha / a;
        _b0 = (1f + alpha * a) / a0;
        _b1 = -2f * cos / a0;
        _b2 = (1f - alpha * a) / a0;
        _a1 = -2f * cos / a0;
        _a2 = (1f - alpha / a) / a0;
    }

    private static float TimeCoefficient(float milliseconds, double sampleRate) =>
        MathF.Exp(-1f / (milliseconds * 0.001f * (float)sampleRate));

    private static float LinearToDb(float linear) => 20f * MathF.Log10(linear);
    private static float DbToLinear(float db) => MathF.Pow(10f, db / 20f);
}
