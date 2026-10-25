namespace SystemCompressor.Dsp;

/// <summary>
/// Defaults are tuned for film playback: a low threshold with a moderate ratio evens out
/// whispered dialogue versus explosions, a slow release avoids audible pumping, and a
/// small speech EQ boost keeps speech intelligible.
/// </summary>
public sealed record CompressorSettings(
    bool Enabled = true,
    float ThresholdDb = -20f,
    float Ratio = 3.5f,
    float AttackMs = 0.1f,
    float ReleaseMs = 250f,
    float MakeupGainDb = 0f,
    float SpeechBoostDb = 2f,
    float SpeechFreqHz = 3000f,
    float KneeDb = 5f)
{
    /// <summary>Upper limit of the gain knob: at most +5 dB boost.</summary>
    public const float MaxMakeupGainDb = 5f;

    public CompressorSettings Validate() => this with
    {
        ThresholdDb = Math.Clamp(ThresholdDb, -60f, 0f),
        Ratio = Math.Clamp(Ratio, 1f, 20f),
        AttackMs = Math.Clamp(AttackMs, 0.1f, 200f),
        ReleaseMs = Math.Clamp(ReleaseMs, 10f, 2000f),
        MakeupGainDb = Math.Clamp(MakeupGainDb, -24f, MaxMakeupGainDb),
        SpeechBoostDb = Math.Clamp(SpeechBoostDb, 0f, 8f),
        SpeechFreqHz = Math.Clamp(SpeechFreqHz, 1000f, 8000f),
        KneeDb = Math.Clamp(KneeDb, 0f, 24f)
    };
}
