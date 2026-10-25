// System Compressor - VST2-ABI plug-in for Equalizer APO.
//
// Equalizer APO (<= 1.4.2) loads only VST 2.x plug-ins, so this DLL implements the
// small VST2 C ABI directly (no Steinberg SDK needed). Settings come from
// C:\ProgramData\SystemCompressor\settings.json, written by the app. A worker thread polls the
// file and publishes values through atomics, so process() never allocates, locks, or
// touches the file system. The same thread writes meter-*.txt for the app's level meter.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <xmmintrin.h>
#include <shlobj.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cwchar>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <string>
#include <thread>

namespace {

// ---- minimal VST2 ABI ------------------------------------------------------
struct AEffect;
using AudioMasterCallback = intptr_t (*)(AEffect*, int32_t, int32_t, intptr_t, void*, float);
using DispatcherProc = intptr_t (*)(AEffect*, int32_t, int32_t, intptr_t, void*, float);
using ProcessProc = void (*)(AEffect*, float**, float**, int32_t);
using SetParameterProc = void (*)(AEffect*, int32_t, float);
using GetParameterProc = float (*)(AEffect*, int32_t);

struct AEffect {
    int32_t magic;
    DispatcherProc dispatcher;
    ProcessProc process;
    SetParameterProc setParameter;
    GetParameterProc getParameter;
    int32_t numPrograms, numParams, numInputs, numOutputs, flags;
    intptr_t resvd1, resvd2;
    int32_t initialDelay, realQualities, offQualities;
    float ioRatio;
    void* object;
    void* user;
    int32_t uniqueID, version;
    ProcessProc processReplacing;
    void* processDoubleReplacing;
    char future[56];
};

constexpr int32_t kMagic = 0x56737450; // 'VstP'
enum : int32_t {
    effOpen = 0, effClose = 1, effGetProgram = 3, effGetProgramName = 5,
    effGetParamLabel = 6, effGetParamDisplay = 7, effGetParamName = 8,
    effSetSampleRate = 10, effMainsChanged = 12, effGetEffectName = 45,
    effGetVendorString = 47, effGetProductString = 48, effGetVendorVersion = 49,
    effCanDo = 51, effGetVstVersion = 58
};
constexpr int32_t kFlagsCanReplacing = 1 << 4;

// ---- parameters (host-visible, normalised 0..1) ----------------------------
enum Param { kThreshold, kRatio, kAttack, kRelease, kGain, kEnabled, kSpeech, kSpeechFreq, kKnee, kParamCount };

struct ParamInfo { const char* name; const char* label; float min, max; };
constexpr ParamInfo kInfo[kParamCount] = {
    {"Threshold", "dB", -60.f, 0.f},   {"Ratio", ":1", 1.f, 20.f},
    {"Attack", "ms", 0.1f, 200.f},     {"Release", "ms", 10.f, 2000.f},
    {"Gain", "dB", -24.f, 5.f},       {"Enabled", "", 0.f, 1.f},
    {"Speech", "dB", 0.f, 8.f},        {"SpeechFreq", "Hz", 1000.f, 8000.f},
    {"Knee", "dB", 0.f, 24.f},
};

float Plain(Param p, float norm) { return kInfo[p].min + std::clamp(norm, 0.f, 1.f) * (kInfo[p].max - kInfo[p].min); }
float Norm(Param p, float plain) { return std::clamp((plain - kInfo[p].min) / (kInfo[p].max - kInfo[p].min), 0.f, 1.f); }

// ---- settings file ---------------------------------------------------------
// Equalizer APO runs inside the Windows audio service (audiodg.exe) under the LOCAL SERVICE
// account, whose %LocalAppData% is not the user's. The app and the plug-in therefore
// share C:\ProgramData\SystemCompressor (the app grants LOCAL SERVICE access to it).
std::wstring DataDir() {
    // Test hook: the test programs point this at a temporary folder so they never touch (or disturb) a real installation.
    wchar_t override_[MAX_PATH];
    const DWORD length = GetEnvironmentVariableW(L"SYSTEMCOMPRESSOR_DATA_DIR", override_, MAX_PATH);
    if (length > 0 && length < MAX_PATH) return std::wstring(override_);
    PWSTR dir = nullptr;
    std::wstring path;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_ProgramData, 0, nullptr, &dir))) {
        path = std::wstring(dir) + L"\\SystemCompressor";
        CoTaskMemFree(dir);
    }
    return path;
}
std::wstring SettingsPath() { const auto d = DataDir(); return d.empty() ? d : d + L"\\settings.json"; }

// Reads a number following "Key": in the System.Text.Json output of CompressorSettings.
bool FindNumber(const std::string& json, const char* key, float& out) {
    auto pos = json.find(std::string("\"") + key + "\"");
    if (pos == std::string::npos) return false;
    pos = json.find(':', pos);
    if (pos == std::string::npos) return false;
    char* end = nullptr;
    const float v = std::strtof(json.c_str() + pos + 1, &end);
    if (end == json.c_str() + pos + 1) return false;
    out = v;
    return true;
}

bool FindBool(const std::string& json, const char* key, bool& out) {
    auto pos = json.find(std::string("\"") + key + "\"");
    if (pos == std::string::npos) return false;
    pos = json.find(':', pos);
    if (pos == std::string::npos) return false;
    const auto rest = json.find_first_not_of(" \t\r\n", pos + 1);
    if (rest == std::string::npos) return false;
    out = json.compare(rest, 4, "true") == 0;
    return json.compare(rest, 4, "true") == 0 || json.compare(rest, 5, "false") == 0;
}

// ---- plug-in instance ------------------------------------------------------
struct Plugin {
    // Shared with the worker thread / host (written outside the audio thread).
    std::atomic<float> plain[kParamCount];
    std::atomic<bool> stop{false};
    // Diagnostics published for the tray app (written by the audio thread, read by the worker).
    std::atomic<uint64_t> blocks{0};
    std::atomic<float> inPeak{0.f}, outPeak{0.f}, minGainDb{0.f};
    std::thread watcher;

    // Audio-thread state.
    double sampleRate = 48000.0;
    float gainDb = 0.f;       // current gain-reduction envelope
    float makeupLin = 1.f;    // smoothed makeup gain
    float hpIn[2] = {0, 0}, hpOut[2] = {0, 0}, hp2Out[2] = {0, 0};   // detector high-pass state
    float z1[2] = {0, 0}, z2[2] = {0, 0};        // presence biquad state
    float lastSpeech = -1.f, lastFreq = -1.f;                     // speech boost the biquad was designed for
    double lastRate = 0.0;
    bool presenceWasOn = false;
    float b0 = 1, b1 = 0, b2 = 0, a1 = 0, a2 = 0;

    Plugin() {
        // Film defaults, identical to CompressorSettings in the app.
        plain[kThreshold] = -20.f; plain[kRatio] = 3.5f; plain[kAttack] = 0.1f;
        plain[kRelease] = 250.f;   plain[kGain] = 0.f;   plain[kEnabled] = 1.f;
        plain[kSpeech] = 2.f;      plain[kSpeechFreq] = 3000.f; plain[kKnee] = 5.f;
    }

    // Clears all audio-thread state (also used when the host resumes processing).
    void ResetState() {
        gainDb = 0.f; makeupLin = 1.f; presenceWasOn = false;
        for (int c = 0; c < 2; ++c) { hpIn[c] = hpOut[c] = hp2Out[c] = 0.f; z1[c] = z2[c] = 0.f; }
    }

    // Stability rule: nothing in here may throw into the host (an exception in the audio service would take it down).
    // Without a worker thread the plug-in simply keeps working with its current values.
    void Start() {
        if (watcher.joinable()) return;   // a second effOpen must not overwrite a running thread (that would terminate the process)
        try { watcher = std::thread([this] { Watch(); }); } catch (...) {}
    }
    void Stop() { stop = true; if (watcher.joinable()) watcher.join(); }

    void Watch() {
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);   // never compete with the audio thread
        try { WatchLoop(); } catch (...) {}
    }

    void WatchLoop() {
        const auto path = SettingsPath();
        FILETIME last{};
        for (int tick = 0; !stop; ++tick) {
            if (tick % 5 == 0) PollSettings(path, last);   // settings: every ~250 ms
            WriteStatus(tick);                             // meter data: up to 20 times per second
            Sleep(50);
        }
        if (statusFile != INVALID_HANDLE_VALUE) CloseHandle(statusFile);
        DeleteFileW(StatusPath().c_str());
    }

    void PollSettings(const std::wstring& path, FILETIME& last) {
        WIN32_FILE_ATTRIBUTE_DATA data{};
        if (path.empty() || !GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &data) ||
            CompareFileTime(&data.ftLastWriteTime, &last) == 0) return;
        // FILE_SHARE_DELETE lets the tray replace the file atomically while we read.
        HANDLE file = CreateFileW(path.c_str(), GENERIC_READ,
                                  FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                  nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return;
        char buffer[8192];
        DWORD got = 0;
        const bool ok = ReadFile(file, buffer, sizeof buffer - 1, &got, nullptr) != 0;
        CloseHandle(file);
        if (ok && Load(std::string(buffer, got))) last = data.ftLastWriteTime;
    }

    uint64_t lastBlocks = 0;
    HANDLE statusFile = INVALID_HANDLE_VALUE;
    std::wstring StatusPath() const {
        wchar_t name[64];
        swprintf(name, 64, L"\\meter-%p.txt", static_cast<const void*>(this));
        return DataDir() + name;
    }

    // Fixed-length record rewritten in place (no truncation, so readers never see an empty file).
    // While audio flows it is refreshed every 50 ms; when idle once per second as a heartbeat.
    void WriteStatus(int tick) {
        const uint64_t now = blocks.load(), delta = now - lastBlocks;
        if (delta == 0 && tick % 20 != 0) return;
        if (statusFile == INVALID_HANDLE_VALUE) {
            const auto dir = DataDir();
            if (dir.empty()) return;
            statusFile = CreateFileW(StatusPath().c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                     nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (statusFile == INVALID_HANDLE_VALUE) return;
        }
        lastBlocks = now;
        const float in = inPeak.exchange(0.f), out = outPeak.exchange(0.f), red = minGainDb.exchange(0.f);
        char text[160];
        const int n = std::snprintf(text, sizeof text,
            "tick=%020llu\nblocks=%010llu\ninDb=%08.1f\noutDb=%08.1f\ngrDb=%08.1f\nenabled=%d\n",
            static_cast<unsigned long long>(GetTickCount64()), static_cast<unsigned long long>(delta),
            20.f * std::log10(std::max(in, 1e-7f)), 20.f * std::log10(std::max(out, 1e-7f)), -red,
            plain[kEnabled] >= 0.5f ? 1 : 0);
        SetFilePointer(statusFile, 0, nullptr, FILE_BEGIN);
        DWORD w; WriteFile(statusFile, text, static_cast<DWORD>(n), &w, nullptr);
    }

    bool Load(const std::string& json) {
        float v; bool b; bool any = false;
        auto set = [&](Param p, float x) {
            if (!std::isfinite(x)) return;   // NaN would poison the gain for good; infinity is clamped below
            plain[p] = std::clamp(x, kInfo[p].min, kInfo[p].max); any = true;
        };
        if (FindNumber(json, "ThresholdDb", v)) set(kThreshold, v);
        if (FindNumber(json, "Ratio", v)) set(kRatio, v);
        if (FindNumber(json, "AttackMs", v)) set(kAttack, v);
        if (FindNumber(json, "ReleaseMs", v)) set(kRelease, v);
        if (FindNumber(json, "MakeupGainDb", v)) set(kGain, v);
        if (FindNumber(json, "SpeechBoostDb", v)) set(kSpeech, v);
        if (FindNumber(json, "SpeechFreqHz", v)) set(kSpeechFreq, v);
        if (FindNumber(json, "KneeDb", v)) set(kKnee, v);
        if (FindBool(json, "Enabled", b)) set(kEnabled, b ? 1.f : 0.f);
        return any;
    }

    static float Coef(float ms, double sr) { return std::exp(-1.f / (ms * 0.001f * static_cast<float>(sr))); }

    // RBJ peaking filter (default 3 kHz): speech EQ boost after compression.
    void DesignPresence(float gainDb, float freqHz) {
        const float a = std::pow(10.f, gainDb / 40.f);
        const float w0 = 2.f * 3.14159265f * freqHz / static_cast<float>(sampleRate);
        const float alpha = std::sin(w0) / (2.f * 0.9f), c = std::cos(w0), a0 = 1.f + alpha / a;
        b0 = (1.f + alpha * a) / a0; b1 = -2.f * c / a0; b2 = (1.f - alpha * a) / a0;
        a1 = -2.f * c / a0;          a2 = (1.f - alpha / a) / a0;
        lastSpeech = gainDb; lastFreq = freqHz; lastRate = sampleRate;
    }

    // Soft-knee transfer curve (knee width 0 = hard knee) -> gain change in dB.
    static float KneeGainDb(float inDb, float threshold, float ratio, float knee) {
        const float over = inDb - threshold, slope = 1.f / ratio - 1.f;
        if (2.f * over < -knee) return 0.f;
        if (knee > 0.f && 2.f * over <= knee) { const float t = over + knee / 2.f; return slope * t * t / (2.f * knee); }
        return slope * over;
    }

    // Safety net for transients that pass before the attack closes: round off smoothly
    // below 0 dBFS instead of hard clipping. Untouched below ~ -1 dBFS.
    static float SoftClip(float x) {
        constexpr float start = 0.89f, range = 1.f - start;
        const float m = std::fabs(x);
        if (m <= start) return x;
        return std::copysign(start + range * std::tanh((m - start) / range), x);
    }

    static void RaiseTo(std::atomic<float>& a, float v) { float o = a.load(); while (v > o && !a.compare_exchange_weak(o, v)) {} }
    static void LowerTo(std::atomic<float>& a, float v) { float o = a.load(); while (v < o && !a.compare_exchange_weak(o, v)) {} }
    void Publish(float in, float out, float gain) {
        blocks.fetch_add(1, std::memory_order_relaxed);
        RaiseTo(inPeak, in); RaiseTo(outPeak, out); LowerTo(minGainDb, gain);
    }

    void Process(float** in, float** out, int32_t frames) {
        if (frames <= 0 || !in || !out || !in[0] || !out[0]) return;
        if (!in[1] || !out[1]) {   // unexpected mono layout: pass channel 0 through unchanged
            if (in[0] != out[0]) std::memcpy(out[0], in[0], sizeof(float) * static_cast<size_t>(frames));
            return;
        }
        // Flush denormals to zero: filter tails in silence must not cost CPU spikes.
        const unsigned oldCsr = _mm_getcsr();
        _mm_setcsr(oldCsr | 0x8040);
        const float threshold = plain[kThreshold], ratio = plain[kRatio], knee = plain[kKnee];
        const float attack = Coef(plain[kAttack], sampleRate), release = Coef(plain[kRelease], sampleRate);
        const float makeupTarget = std::pow(10.f, plain[kGain] / 20.f);
        const bool enabled = plain[kEnabled] >= 0.5f;
        const float smooth = Coef(10.f, sampleRate); // 10 ms ramp: no zipper noise on the knob
        const float speech = plain[kSpeech];
        const bool presence = speech > 0.05f;
        const float freq = plain[kSpeechFreq];
        if (presence && !presenceWasOn) { z1[0] = z1[1] = z2[0] = z2[1] = 0.f; }   // no stale EQ state -> no click
        presenceWasOn = presence;
        if (presence && (speech != lastSpeech || freq != lastFreq || sampleRate != lastRate)) DesignPresence(speech, freq);
        // Detector ignores content below ~120 Hz so explosions / sub-bass do not cause pumping.
        const float hp = 1.f / (1.f + 2.f * 3.14159265f * 120.f / static_cast<float>(sampleRate));

        float blockIn = 0.f, blockOut = 0.f, blockGain = 0.f;
        for (int32_t i = 0; i < frames; ++i) {
            float x[2] = {in[0][i], in[1][i]};
            // A single NaN/Inf from the host would poison the filter states for good: drop it.
            if (!std::isfinite(x[0])) x[0] = 0.f;
            if (!std::isfinite(x[1])) x[1] = 0.f;
            blockIn = std::max(blockIn, std::max(std::fabs(x[0]), std::fabs(x[1])));
            if (!enabled) { out[0][i] = x[0]; out[1][i] = x[1]; blockOut = blockIn; continue; }
            float peak = 1e-7f;
            for (int c = 0; c < 2; ++c) {
                const float y1 = hp * (hpOut[c] + x[c] - hpIn[c]);
                const float y2 = hp * (hp2Out[c] + y1 - hpOut[c]);
                hpIn[c] = x[c]; hpOut[c] = y1; hp2Out[c] = y2;
                peak = std::max(peak, std::fabs(y2));
            }
            const float target = KneeGainDb(20.f * std::log10(peak), threshold, ratio, knee);
            const float coef = target < gainDb ? attack : release;
            gainDb = target + coef * (gainDb - target);
            makeupLin = makeupTarget + smooth * (makeupLin - makeupTarget);
            blockGain = std::min(blockGain, gainDb);
            const float g = std::pow(10.f, gainDb / 20.f) * makeupLin;
            for (int c = 0; c < 2; ++c) {
                float v = x[c] * g;
                if (presence) {
                    const float o = b0 * v + z1[c];
                    z1[c] = b1 * v - a1 * o + z2[c];
                    z2[c] = b2 * v - a2 * o;
                    v = o;
                }
                out[c][i] = SoftClip(v);
                blockOut = std::max(blockOut, std::fabs(out[c][i]));
            }
        }
        Publish(blockIn, blockOut, blockGain);
        _mm_setcsr(oldCsr);
    }
};

Plugin* Self(AEffect* e) { return static_cast<Plugin*>(e->object); }

void CopyStr(void* dst, const char* src, size_t max = 64) { if (!dst) return; std::strncpy(static_cast<char*>(dst), src, max - 1); static_cast<char*>(dst)[max - 1] = 0; }

intptr_t Dispatch(AEffect* e, int32_t op, int32_t index, intptr_t value, void* ptr, float) {
    auto* p = Self(e);
    switch (op) {
    case effOpen: p->Start(); return 0;
    case effClose: p->Stop(); delete p; e->object = nullptr; delete e; return 0;
    case effSetSampleRate: return 0; // value is not passed for this opcode; opt is (below)
    case effMainsChanged: if (value) p->ResetState(); return 0;   // resume: start from a clean state
    case effGetProgram: return 0;
    case effGetProgramName: CopyStr(ptr, "Default"); return 0;
    case effGetParamName: if (index >= 0 && index < kParamCount) CopyStr(ptr, kInfo[index].name); return 0;
    case effGetParamLabel: if (index >= 0 && index < kParamCount) CopyStr(ptr, kInfo[index].label); return 0;
    case effGetParamDisplay:
        if (index >= 0 && index < kParamCount) { char b[32]; std::snprintf(b, sizeof b, "%.1f", p->plain[index].load()); CopyStr(ptr, b); }
        return 0;
    case effGetEffectName: case effGetProductString: CopyStr(ptr, "System Compressor"); return 1;
    case effGetVendorString: CopyStr(ptr, "SystemCompressor"); return 1;
    case effGetVendorVersion: return 1000;
    case effGetVstVersion: return 2400;
    case effCanDo: return 0;
    default: return 0;
    }
}

// effSetSampleRate delivers the rate in `opt`; handle it here to keep Dispatch simple.
intptr_t DispatchWrapper(AEffect* e, int32_t op, int32_t index, intptr_t value, void* ptr, float opt) {
    if (op == effSetSampleRate && e->object) {
        Self(e)->sampleRate = (std::isfinite(opt) && opt >= 8000.f && opt <= 768000.f) ? opt : 48000.0;
        return 0;
    }
    return Dispatch(e, op, index, value, ptr, opt);
}

void ProcessReplacing(AEffect* e, float** in, float** out, int32_t frames) { Self(e)->Process(in, out, frames); }
void SetParameter(AEffect* e, int32_t i, float v) { if (i >= 0 && i < kParamCount && std::isfinite(v)) Self(e)->plain[i] = Plain(static_cast<Param>(i), v); }
float GetParameter(AEffect* e, int32_t i) { return i >= 0 && i < kParamCount ? Norm(static_cast<Param>(i), Self(e)->plain[i]) : 0.f; }

} // namespace

extern "C" __declspec(dllexport) AEffect* VSTPluginMain(AudioMasterCallback) {
    try {
    std::unique_ptr<AEffect> e(new AEffect{});
    e->magic = kMagic;
    e->dispatcher = DispatchWrapper;
    e->process = ProcessReplacing;
    e->processReplacing = ProcessReplacing;
    e->setParameter = SetParameter;
    e->getParameter = GetParameter;
    e->numPrograms = 1;
    e->numParams = kParamCount;
    e->numInputs = 2;
    e->numOutputs = 2;
    e->flags = kFlagsCanReplacing;
    e->uniqueID = 0x53437031; // 'SCp1'
    e->version = 1000;
    e->object = new Plugin();
    return e.release();
    } catch (...) {
        return nullptr;   // the host reports "plug-in could not be loaded" instead of crashing
    }
}

extern "C" __declspec(dllexport) AEffect* main_plugin(AudioMasterCallback cb) { return VSTPluginMain(cb); }
