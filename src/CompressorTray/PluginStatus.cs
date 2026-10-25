using System.Globalization;

namespace SystemCompressor.Tray;

/// <summary>
/// What the plug-in (running inside the Windows audio service) reports in meter-*.txt: a fixed-length
/// record rewritten up to 20 times per second while audio flows (once a second when idle).
/// Several instances may exist (one per audio graph); the fresh ones are merged.
/// </summary>
public readonly record struct PluginStatus(bool PluginLoaded, bool Processing, float InputDb, float OutputDb, float ReductionDb)
{
    private const float Silence = -120f;
    private static string[] _files = [];
    private static long _filesScannedAt = -100_000;   // far in the past, but small enough that 'now - x' cannot overflow

    public static PluginStatus Read()
    {
        var now = Environment.TickCount64;
        try
        {
            if (now - _filesScannedAt > 1000)
            {
                _files = Directory.GetFiles(CompressorConfiguration.DirectoryPath, "meter-*.txt");
                _filesScannedAt = now;
            }
        }
        catch (IOException) { _files = []; }
        catch (UnauthorizedAccessException) { _files = []; }

        var loaded = false;
        var processing = false;
        float input = Silence, output = Silence, reduction = 0f;
        foreach (var file in _files)
        {
            if (!TryParse(file, out var tick, out var blocks, out var inDb, out var outDb, out var grDb)) continue;
            var age = now - tick;
            if (age > 60_000) { TryDelete(file); continue; }   // left behind by an audio service that crashed
            if (age is < -500 or > 3500) continue;   // stale file left behind by a dead instance
            loaded = true;
            if (blocks > 0 && age < 400)
            {
                processing = true;
                input = Math.Max(input, inDb);
                output = Math.Max(output, outDb);
                reduction = Math.Max(reduction, grDb);
            }
        }
        return new PluginStatus(loaded, processing, input, output, reduction);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool TryParse(string path, out long tick, out long blocks, out float inDb, out float outDb, out float grDb)
    {
        tick = blocks = 0;
        inDb = outDb = Silence;
        grDb = 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var values = new Dictionary<string, double>();
            while (reader.ReadLine() is { } line)
            {
                var parts = line.Split('=');
                if (parts.Length == 2 && double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    values[parts[0].Trim()] = v;
            }
            if (!values.TryGetValue("tick", out var t)) return false;
            tick = (long)t;
            blocks = (long)values.GetValueOrDefault("blocks");
            inDb = (float)values.GetValueOrDefault("inDb", Silence);
            outDb = (float)values.GetValueOrDefault("outDb", Silence);
            grDb = (float)values.GetValueOrDefault("grDb");
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public string Describe(bool enabled) =>
        !PluginLoaded ? "Plugin not responding – check Equalizer APO / output device"
        : !Processing ? (enabled ? "Ready – no audio playing" : "Off – audio passes through unchanged")
        : !enabled ? "Off – audio passes through unchanged"
        : ReductionDb < 0.5f ? "Active – level is below the threshold"
        : $"Working – level reduced by up to {ReductionDb:0.0} dB";
}
