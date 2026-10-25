using SystemCompressor.Dsp;

const int rate = 48_000;

static float[] Sine(double frequency, float amplitude, int rate, int seconds = 1)
{
    var data = new float[rate * seconds * 2];
    for (var i = 0; i < rate * seconds; i++)
        data[2 * i] = data[2 * i + 1] = amplitude * (float)Math.Sin(2 * Math.PI * frequency * i / rate);
    return data;
}

static float TailPeak(float[] data) => data.AsSpan(data.Length - 4_000).ToArray().Max(MathF.Abs);

// 1 kHz at 0 dBFS, threshold -12 dB, 4:1 -> settles near -9 dBFS (0.355).
var compressor = new StereoCompressor();
compressor.Prepare(rate, new CompressorSettings(
    ThresholdDb: -12f, Ratio: 4f, AttackMs: 0.1f, ReleaseMs: 100f, MakeupGainDb: 0f, SpeechBoostDb: 0f));
var signal = Sine(1000, 1f, rate);
compressor.Process(signal, 2);
var finalPeak = TailPeak(signal);
if (finalPeak is > 0.38f or < 0.33f)
    throw new InvalidOperationException($"Unexpected compressed peak: {finalPeak:F3}");

// Makeup gain knob: +5 dB scales the compressed level by 1.78.
var gained = new StereoCompressor();
gained.Prepare(rate, new CompressorSettings(
    ThresholdDb: -12f, Ratio: 4f, AttackMs: 0.1f, ReleaseMs: 100f, MakeupGainDb: 5f, SpeechBoostDb: 0f));
var gainedSignal = Sine(1000, 1f, rate);
gained.Process(gainedSignal, 2);
var ratioToPlain = TailPeak(gainedSignal) / finalPeak;
if (ratioToPlain is < 1.74f or > 1.82f)
    throw new InvalidOperationException($"Gain knob wrong: x{ratioToPlain:F2}, expected ~x1.78");

// Film behaviour: loud sub-bass must not pull the level of speech-band content down.
var film = new StereoCompressor();
film.Prepare(rate, new CompressorSettings(ThresholdDb: -20f, Ratio: 4f, MakeupGainDb: 0f, SpeechBoostDb: 0f));
var bass = Sine(40, 0.8f, rate);
film.Process(bass, 2);
if (TailPeak(bass) < 0.7f)
    throw new InvalidOperationException($"40 Hz rumble was compressed: {TailPeak(bass):F3}");

// Speech presence boost: +6 dB at 3 kHz on a quiet tone (below threshold, so compression is idle).
var presence = new StereoCompressor();
presence.Prepare(rate, new CompressorSettings(ThresholdDb: -10f, Ratio: 2f, MakeupGainDb: 0f, SpeechBoostDb: 6f));
var voice = Sine(3000, 0.05f, rate);
presence.Process(voice, 2);
var boost = TailPeak(voice) / 0.05f;
if (boost is < 1.9f or > 2.1f)
    throw new InvalidOperationException($"Presence boost wrong: x{boost:F2}, expected ~x2.0");

// Quality 1 - neutral: below the threshold with EQ off and 0 dB gain the signal passes bit-exactly.
var neutral = new StereoCompressor();
neutral.Prepare(rate, new CompressorSettings(ThresholdDb: 0f, MakeupGainDb: 0f, SpeechBoostDb: 0f));
var quiet = Sine(440, 0.5f, rate);
var quietCopy = (float[])quiet.Clone();
neutral.Process(quiet, 2);
if (!quiet.AsSpan().SequenceEqual(quietCopy))
    throw new InvalidOperationException("Not neutral below threshold: signal was altered.");

// Quality 2 - zero latency: an impulse appears in the very same sample (no look-ahead buffer).
var latency = new StereoCompressor();
latency.Prepare(rate, new CompressorSettings(SpeechBoostDb: 0f));
var impulse = new float[200];
impulse[0] = impulse[1] = 0.5f;
latency.Process(impulse, 2);
if (impulse[0] == 0f || impulse.Skip(2).Any(v => v != 0f))
    throw new InvalidOperationException("Impulse was delayed or smeared (not zero-latency).");

// Quality 3 - no hard clipping: a full-scale burst after silence with the maximum +5 dB makeup and +8 dB EQ never exceeds full scale.
var safe = new StereoCompressor();
safe.Prepare(rate, new CompressorSettings(MakeupGainDb: 5f, SpeechBoostDb: 8f));
var burst = new float[rate * 2];
for (var i = 20_000; i < 24_000; i++) burst[2 * i] = burst[2 * i + 1] = (i % 2 == 0 ? 1f : -1f);
var loud = Sine(1000, 1f, rate);
safe.Process(burst, 2);
safe.Process(loud, 2);
var overshoot = Math.Max(burst.Max(MathF.Abs), loud.Max(MathF.Abs));
if (overshoot > 1f)
    throw new InvalidOperationException($"Output exceeded full scale: {overshoot:F4}");

// Quality 4 - low distortion while compressing: THD of a 1 kHz tone at -20 dBFS (threshold -28 dB).
var clean = new StereoCompressor();
clean.Prepare(rate, new CompressorSettings(MakeupGainDb: 0f, SpeechBoostDb: 0f));
var tone = Sine(1000, 0.1f, rate, seconds: 2);
clean.Process(tone, 2);
double Goertzel(float[] data, double frequency, int from, int count)
{
    double re = 0, im = 0;
    for (var i = 0; i < count; i++)
    {
        var angle = 2 * Math.PI * frequency * i / rate;
        var v = data[2 * (from + i)];
        re += v * Math.Cos(angle);
        im += v * Math.Sin(angle);
    }
    return Math.Sqrt(re * re + im * im) * 2 / count;
}
var fundamental = Goertzel(tone, 1000, rate, rate);
double harmonics = 0;
for (var h = 2; h <= 6; h++) harmonics += Math.Pow(Goertzel(tone, 1000 * h, rate, rate), 2);
var thdPercent = Math.Sqrt(harmonics) / fundamental * 100;
if (thdPercent > 0.1)
    throw new InvalidOperationException($"Distortion too high: THD {thdPercent:F4} %");

// Knee: right at the threshold a hard knee leaves the level alone, a 12 dB knee already reduces it by ~1.1 dB.
float KneeRatio(float knee)
{
    var c = new StereoCompressor();
    c.Prepare(rate, new CompressorSettings(ThresholdDb: -20f, Ratio: 4f, AttackMs: 0.1f, ReleaseMs: 100f,
        MakeupGainDb: 0f, SpeechBoostDb: 0f, KneeDb: knee));
    var s = Sine(1000, 0.1f, rate);
    c.Process(s, 2);
    return TailPeak(s) / 0.1f;
}
var hardKnee = KneeRatio(0f);
var softKnee = KneeRatio(12f);
if (hardKnee < 0.98f || softKnee is < 0.84f or > 0.92f || softKnee >= hardKnee)
    throw new InvalidOperationException($"Knee wrong: hard x{hardKnee:F3} (expect ~1.0), 12 dB x{softKnee:F3} (expect ~0.88)");

// Robustness: a NaN/Infinity burst from the host must not poison the filters for good.
var robust = new StereoCompressor();
robust.Prepare(rate, new CompressorSettings());
var poisoned = new float[400];
poisoned[0] = float.NaN; poisoned[10] = float.PositiveInfinity; poisoned[21] = float.NegativeInfinity;
robust.Process(poisoned, 2);
var afterwards = Sine(1000, 0.3f, rate);
robust.Process(afterwards, 2);
if (poisoned.Any(v => !float.IsFinite(v)) || afterwards.Any(v => !float.IsFinite(v)))
    throw new InvalidOperationException("NaN/Infinity input poisoned the compressor.");

var bypass = new StereoCompressor();
bypass.Prepare(rate, new CompressorSettings(Enabled: false));
var untouched = new[] { 0.25f, -0.25f };
bypass.Process(untouched, 2);
if (untouched[0] != 0.25f || untouched[1] != -0.25f)
    throw new InvalidOperationException("Disabled compressor modified audio.");

Console.WriteLine(PluginCrossCheck.Run(rate));
Console.WriteLine($"DSP smoke test passed; peak {finalPeak:F3}, gain x{ratioToPlain:F2}, presence x{boost:F2}, knee x{hardKnee:F2}/x{softKnee:F2}, THD {thdPercent:F4} %, max {overshoot:F3}.");
