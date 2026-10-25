using System.Runtime.InteropServices;
using SystemCompressor.Dsp;

/// <summary>
/// Loads the real plug-in DLL (build\vst2\Release\SystemCompressor.dll) like a VST2 host and checks that it
/// produces the same audio as the C# reference <see cref="StereoCompressor"/>. This guards the two
/// implementations against drifting apart. Skipped when the DLL has not been built.
/// The plug-in is pointed at an empty temporary data folder (SYSTEMCOMPRESSOR_DATA_DIR), so an installed copy and its
/// settings are never touched.
/// </summary>
public static class PluginCrossCheck
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AEffect
    {
        public int Magic;
        public IntPtr Dispatcher, Process, SetParameter, GetParameter;
        public int NumPrograms, NumParams, NumInputs, NumOutputs, Flags;
        public IntPtr Reserved1, Reserved2;
        public int InitialDelay, RealQualities, OffQualities;
        public float IoRatio;
        public IntPtr Object, User;
        public int UniqueId, Version;
        public IntPtr ProcessReplacing, ProcessDoubleReplacing;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr MainProc(IntPtr audioMaster);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr DispatcherProc(IntPtr e, int op, int index, IntPtr value, IntPtr ptr, float opt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetParameterProc(IntPtr e, int index, float value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ProcessProc(IntPtr e, IntPtr inputs, IntPtr outputs, int frames);

    // VST parameter order and ranges (must match kInfo in SystemCompressor.cpp).
    private static readonly (float Min, float Max)[] Ranges =
    [
        (-60f, 0f), (1f, 20f), (0.1f, 200f), (10f, 2000f), (-24f, 5f), (0f, 1f), (0f, 8f), (1000f, 8000f), (0f, 24f)
    ];

    public static string Run(int sampleRate)
    {
        var root = AppContext.BaseDirectory;
        string? dll = null;
        for (var dir = new DirectoryInfo(root); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "build", "vst2", "Release", "SystemCompressor.dll");
            if (File.Exists(candidate)) { dll = candidate; break; }
        }
        if (dll is null) return "plug-in cross-check skipped (build\\vst2\\Release\\SystemCompressor.dll not built)";

        var isolated = Path.Combine(Path.GetTempPath(), "SystemCompressorCrossCheck-" + Environment.ProcessId);
        Directory.CreateDirectory(isolated);
        Environment.SetEnvironmentVariable("SYSTEMCOMPRESSOR_DATA_DIR", isolated);
        try
        {
            var scenarios = new (string Name, CompressorSettings Settings, bool SetAll)[]
            {
                ("film defaults", new CompressorSettings(), false),
                ("strong compression, EQ at 2 kHz, +5 dB", new CompressorSettings(ThresholdDb: -40f, Ratio: 12f, AttackMs: 0.5f, ReleaseMs: 80f, MakeupGainDb: 5f, SpeechBoostDb: 6f, SpeechFreqHz: 2000f, KneeDb: 12f), true),
                ("hard knee, EQ off, -6 dB", new CompressorSettings(ThresholdDb: -24f, Ratio: 4f, AttackMs: 10f, ReleaseMs: 400f, MakeupGainDb: -6f, SpeechBoostDb: 0f, KneeDb: 0f), true),
                ("disabled", new CompressorSettings(Enabled: false), true),
            };
            var worst = 0.0;
            foreach (var (name, settings, setAll) in scenarios)
            {
                var difference = Compare(dll, sampleRate, settings, setAll);
                if (difference > 2e-4)
                    throw new InvalidOperationException($"Plug-in and reference differ in scenario '{name}': max |diff| = {difference:E2}");
                worst = Math.Max(worst, difference);
            }
            // Negative control: with a deliberately wrong reference the comparison must fail, otherwise it is blind.
            var control = Compare(dll, sampleRate, new CompressorSettings(), false, new CompressorSettings(ThresholdDb: -10f));
            if (control < 1e-3)
                throw new InvalidOperationException($"Cross-check is blind: a wrong reference differs only by {control:E2}");
            return $"plug-in == reference in {scenarios.Length} scenarios (max |diff| {worst:E1}); negative control detects a wrong reference (|diff| {control:E1})";
        }
        finally
        {
            Environment.SetEnvironmentVariable("SYSTEMCOMPRESSOR_DATA_DIR", null);
            try { Directory.Delete(isolated, true); } catch (IOException) { }
        }
    }

    private static double Compare(string dll, int sampleRate, CompressorSettings settings, bool setAll, CompressorSettings? referenceSettings = null)
    {
        var library = NativeLibrary.Load(dll);
        try
        {
            var main = Marshal.GetDelegateForFunctionPointer<MainProc>(NativeLibrary.GetExport(library, "VSTPluginMain"));
            var effectPointer = main(IntPtr.Zero);
            if (effectPointer == IntPtr.Zero) throw new InvalidOperationException("VSTPluginMain returned null");
            var effect = Marshal.PtrToStructure<AEffect>(effectPointer);
            var dispatch = Marshal.GetDelegateForFunctionPointer<DispatcherProc>(effect.Dispatcher);
            var setParameter = Marshal.GetDelegateForFunctionPointer<SetParameterProc>(effect.SetParameter);
            var process = Marshal.GetDelegateForFunctionPointer<ProcessProc>(effect.ProcessReplacing);

            dispatch(effectPointer, 0, 0, IntPtr.Zero, IntPtr.Zero, 0f);                       // effOpen
            dispatch(effectPointer, 10, 0, IntPtr.Zero, IntPtr.Zero, sampleRate);              // effSetSampleRate
            if (setAll)
            {
                var values = new[] { settings.ThresholdDb, settings.Ratio, settings.AttackMs, settings.ReleaseMs,
                    settings.MakeupGainDb, settings.Enabled ? 1f : 0f, settings.SpeechBoostDb, settings.SpeechFreqHz, settings.KneeDb };
                for (var i = 0; i < values.Length; i++)
                    setParameter(effectPointer, i, (values[i] - Ranges[i].Min) / (Ranges[i].Max - Ranges[i].Min));
            }

            var signal = FilmLikeSignal(sampleRate, seconds: 6);             // interleaved stereo
            var expected = (float[])signal.Clone();
            var reference = new StereoCompressor();
            reference.Prepare(sampleRate, referenceSettings ?? settings);
            reference.Process(expected, 2);

            const int block = 480;
            var left = Marshal.AllocHGlobal(block * sizeof(float));
            var right = Marshal.AllocHGlobal(block * sizeof(float));
            var outLeft = Marshal.AllocHGlobal(block * sizeof(float));
            var outRight = Marshal.AllocHGlobal(block * sizeof(float));
            var inputs = Marshal.AllocHGlobal(2 * IntPtr.Size);
            var outputs = Marshal.AllocHGlobal(2 * IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(inputs, 0, left); Marshal.WriteIntPtr(inputs, IntPtr.Size, right);
                Marshal.WriteIntPtr(outputs, 0, outLeft); Marshal.WriteIntPtr(outputs, IntPtr.Size, outRight);
                var l = new float[block]; var r = new float[block]; var ol = new float[block]; var or = new float[block];
                var worst = 0.0;
                for (var frame = 0; frame < signal.Length / 2; frame += block)
                {
                    var n = Math.Min(block, signal.Length / 2 - frame);
                    for (var i = 0; i < n; i++) { l[i] = signal[2 * (frame + i)]; r[i] = signal[2 * (frame + i) + 1]; }
                    Marshal.Copy(l, 0, left, n); Marshal.Copy(r, 0, right, n);
                    process(effectPointer, inputs, outputs, n);
                    Marshal.Copy(outLeft, ol, 0, n); Marshal.Copy(outRight, or, 0, n);
                    for (var i = 0; i < n; i++)
                    {
                        worst = Math.Max(worst, Math.Abs(ol[i] - expected[2 * (frame + i)]));
                        worst = Math.Max(worst, Math.Abs(or[i] - expected[2 * (frame + i) + 1]));
                    }
                }
                dispatch(effectPointer, 1, 0, IntPtr.Zero, IntPtr.Zero, 0f);                   // effClose
                return worst;
            }
            finally
            {
                foreach (var p in new[] { left, right, outLeft, outRight, inputs, outputs }) Marshal.FreeHGlobal(p);
            }
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    /// <summary>Deterministic stand-in for a film soundtrack: quiet speech-like bursts, loud effects, sub-bass, silence, clicks.</summary>
    private static float[] FilmLikeSignal(int rate, int seconds)
    {
        var frames = rate * seconds;
        var data = new float[frames * 2];
        uint seed = 12345;
        float Noise() { seed = seed * 1664525u + 1013904223u; return (seed >> 8) / (float)(1 << 24) * 2f - 1f; }
        for (var i = 0; i < frames; i++)
        {
            var t = i / (double)rate;
            var segment = (int)(t / 0.75) % 8;
            double v = segment switch
            {
                0 => 0.03 * Math.Sin(2 * Math.PI * 220 * t) * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 4 * t)) + 0.02 * Math.Sin(2 * Math.PI * 1800 * t),
                1 => 0.25 * Math.Sin(2 * Math.PI * 300 * t) * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 3 * t)) + 0.1 * Noise(),
                2 => 0.9 * Noise() * Math.Exp(-((t % 0.75) * 6)),                          // explosion
                3 => 0.7 * Math.Sin(2 * Math.PI * 45 * t),                                 // sub-bass
                4 => 0.0,                                                                  // silence
                5 => 0.12 * Math.Sin(2 * Math.PI * 3000 * t) + 0.05 * Noise(),
                6 => (i % 4800 == 0) ? 1.0 : 0.01 * Noise(),                               // clicks
                _ => 0.5 * Math.Sin(2 * Math.PI * 1000 * t),
            };
            data[2 * i] = (float)v;
            data[2 * i + 1] = (float)(v * 0.8 + 0.01 * Noise());                           // slightly different right channel
        }
        return data;
    }
}
