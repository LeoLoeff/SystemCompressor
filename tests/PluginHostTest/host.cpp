// Plug-in host test: loads SystemCompressor.dll like Equalizer APO would and checks its behaviour.
//   cmake -S tests/PluginHostTest -B build/hosttest -G "Visual Studio 17 2022" -A x64
//   cmake --build build/hosttest --config Release
//   build\hosttest\Release\host.exe build\vst2\Release\SystemCompressor.dll
// The test runs isolated in %TEMP%\SystemCompressorTest (via SYSTEMCOMPRESSOR_DATA_DIR); an installed System Compressor is not affected.
// Optional: host.exe <dll> --soak <seconds> runs the stress test for longer (default 8 s).
#define NOMINMAX
#include <windows.h>
#include <cstdio>
#include <cstdint>
#include <cmath>
#include <vector>
#include <string>
#include <algorithm>
#include <random>
#include <psapi.h>
struct AEffect;
typedef intptr_t (*Disp)(AEffect*,int32_t,int32_t,intptr_t,void*,float);
typedef void (*Proc)(AEffect*,float**,float**,int32_t);
struct AEffect{int32_t magic;Disp dispatcher;Proc process;void*sp;void*gp;int32_t np,nparams,ni,no,flags;intptr_t r1,r2;int32_t d,q1,q2;float io;void*obj;void*user;int32_t id,ver;Proc pr;void*pdr;char f[56];};
static int fails=0;
#define CHECK(c,...) do{ if(!(c)){ printf("FAIL: "); printf(__VA_ARGS__); printf("\n"); fails++; } else { printf("ok:   "); printf(__VA_ARGS__); printf("\n"); } }while(0)
static std::wstring path;
static void WriteSettings(const char* json){ std::string tmp; HANDLE f=CreateFileW((path+L".t").c_str(),GENERIC_WRITE,0,0,CREATE_ALWAYS,0,0); DWORD w; WriteFile(f,json,(DWORD)strlen(json),&w,0); CloseHandle(f); MoveFileExW((path+L".t").c_str(),path.c_str(),MOVEFILE_REPLACE_EXISTING); }
static std::vector<float> Run(AEffect*e,const std::vector<float>&x,int block){
  std::vector<float> y(x.size()), o2(block);
  for(size_t p=0;p<x.size();p+=block){ int n=(int)std::min<size_t>(block,x.size()-p);
    float*in[2]={const_cast<float*>(x.data())+p,const_cast<float*>(x.data())+p}; float*out[2]={y.data()+p,o2.data()};
    e->pr(e,in,out,n);} return y; }
static AEffect* Make(HMODULE h){ auto m=(AEffect*(*)(void*))GetProcAddress(h,"VSTPluginMain"); AEffect*e=m(nullptr); e->dispatcher(e,0,0,0,0,0); e->dispatcher(e,10,0,0,0,48000.f); return e; }
static float Peak(const std::vector<float>&v,size_t n=2000){ return *std::max_element(v.end()-n,v.end()); }
int main(int argc, char**argv){
  double soakSeconds = 8; for (int i = 1; i + 1 < argc; ++i) if (std::string(argv[i]) == "--soak") soakSeconds = atof(argv[i + 1]);
  wchar_t buf[MAX_PATH]; GetEnvironmentVariableW(L"TEMP",buf,MAX_PATH); path=std::wstring(buf)+L"/SystemCompressorTest/settings.json";
  SetEnvironmentVariableW(L"SYSTEMCOMPRESSOR_DATA_DIR",(std::wstring(buf)+L"/SystemCompressorTest").c_str());   // isolated: the real installation is never touched
  CreateDirectoryW((std::wstring(buf)+L"/SystemCompressorTest").c_str(),0);
  DeleteFileW(path.c_str());
  HMODULE h=LoadLibraryA(argv[1]);
  const int N=96000; std::vector<float> sine(N); for(int i=0;i<N;i++) sine[i]=0.9f*sinf(6.2831853f*1000*i/48000);

  // 1. defaults, no file
  { AEffect*e=Make(h); Sleep(400); auto y=Run(e,sine,N); CHECK(e->nparams==9,"9 parameters"); CHECK(Peak(y)<1.0f&&Peak(y)>0.05f,"defaults compress, peak %.3f",Peak(y)); e->dispatcher(e,1,0,0,0,0);}
  // 2. block-size independence
  WriteSettings("{\"Settings\":{\"Enabled\":true,\"ThresholdDb\":-20,\"Ratio\":4,\"AttackMs\":2,\"ReleaseMs\":120,\"MakeupGainDb\":0,\"SpeechBoostDb\":3,\"SpeechFreqHz\":3000}}");
  { AEffect*a=Make(h),*b=Make(h); Sleep(500); auto ya=Run(a,sine,N), yb=Run(b,sine,37); double d=0; for(int i=0;i<N;i++) d=std::max(d,(double)fabsf(ya[i]-yb[i]));
    CHECK(d<1e-6,"block-size independent (max diff %.2e)",d); a->dispatcher(a,1,0,0,0,0); b->dispatcher(b,1,0,0,0,0);}
  // 3. live reload: gain changes while running
  { AEffect*e=Make(h); Sleep(400); auto y1=Run(e,sine,512); float p1=Peak(y1);
    WriteSettings("{\"Settings\":{\"Enabled\":true,\"ThresholdDb\":-20,\"Ratio\":4,\"AttackMs\":2,\"ReleaseMs\":120,\"MakeupGainDb\":-6,\"SpeechBoostDb\":3,\"SpeechFreqHz\":3000}}");
    Sleep(600); auto y2=Run(e,sine,512); float p2=Peak(y2); CHECK(fabsf(p2/p1-0.501f)<0.03f,"live reload: -6 dB gain applied (x%.3f)",p2/p1); e->dispatcher(e,1,0,0,0,0);}
  // 4. disabled = exact pass-through
  WriteSettings("{\"Settings\":{\"Enabled\":false,\"ThresholdDb\":-20}}");
  { AEffect*e=Make(h); Sleep(500); auto y=Run(e,sine,256); CHECK(y==sine,"disabled: bit-exact pass-through"); e->dispatcher(e,1,0,0,0,0);}
  // 5. garbage / partial file keeps previous values and does not crash
  WriteSettings("{\"Settings\":{\"Enabled\":true,\"ThresholdDb\":-20,\"Ratio\":4,\"MakeupGainDb\":0,\"SpeechBoostDb\":0}}");
  { AEffect*e=Make(h); Sleep(500); auto y1=Run(e,sine,256); WriteSettings("{\"Settin"); Sleep(600); auto y2=Run(e,sine,256);
    CHECK(fabsf(Peak(y1)-Peak(y2))<0.01f,"truncated file ignored (%.3f vs %.3f)",Peak(y1),Peak(y2));
    WriteSettings("{\"Settings\":{\"ThresholdDb\":-999,\"Ratio\":-5,\"MakeupGainDb\":500,\"SpeechBoostDb\":99,\"SpeechFreqHz\":5}}"); Sleep(600); auto y3=Run(e,sine,256);
    bool finite=true; for(float v:y3) if(!std::isfinite(v)||fabsf(v)>1.0001f) finite=false; CHECK(finite,"out-of-range values are clamped, output finite and <= 1.0");
    e->dispatcher(e,1,0,0,0,0);}
  // 6. zero frames, silence, NaN-free after silence
  { AEffect*e=Make(h); Sleep(300); float z=0; float*in[2]={&z,&z},*out[2]={&z,&z}; e->pr(e,in,out,0);
    std::vector<float> sil(48000,0.f); auto y=Run(e,sil,480); bool zero=true; for(float v:y) if(v!=0.f) zero=false; CHECK(zero,"silence stays silence");
    auto y2=Run(e,sine,480); CHECK(std::isfinite(Peak(y2)),"signal after silence is finite"); e->dispatcher(e,1,0,0,0,0);}
  // 7. rapid concurrent writes (tray saving) while plug-in polls and processes
  { AEffect*e=Make(h); Sleep(300); bool ok=true; for(int i=0;i<150;i++){ char j[200]; sprintf(j,"{\"Settings\":{\"Enabled\":true,\"ThresholdDb\":-%d,\"Ratio\":3,\"MakeupGainDb\":%d,\"SpeechBoostDb\":2}}",20+i%20,(i%6)-3); WriteSettings(j); auto y=Run(e,std::vector<float>(sine.begin(),sine.begin()+4800),256); for(float v:y) if(!std::isfinite(v)||fabsf(v)>1.0001f) ok=false; Sleep(5);}
    CHECK(ok,"150 rapid setting rewrites: stable, finite"); e->dispatcher(e,1,0,0,0,0);}
  // 9. knee: at the threshold a hard knee leaves the level, a 12 dB knee reduces it by ~1 dB
  { auto peakWith=[&](int knee){ char j[300]; sprintf(j,"{\"Settings\":{\"Enabled\":true,\"ThresholdDb\":-20,\"Ratio\":4,\"AttackMs\":0.1,\"ReleaseMs\":100,\"MakeupGainDb\":0,\"SpeechBoostDb\":0,\"KneeDb\":%d}}",knee); WriteSettings(j);
      AEffect*e=Make(h); Sleep(500); std::vector<float> q(N); for(int i=0;i<N;i++) q[i]=0.1f*sinf(6.2831853f*1000*i/48000); auto y=Run(e,q,512); float p=Peak(y)/0.1f; e->dispatcher(e,1,0,0,0,0); return p; };
    float hard=peakWith(0), soft=peakWith(12); CHECK(hard>0.98f&&soft>0.84f&&soft<0.92f&&soft<hard,"knee in plug-in: hard x%.3f, 12 dB x%.3f",hard,soft); }
  // 10. robustness: NaN/Inf input, invalid parameter indices, null buffers, suspend/resume
  { AEffect*e=Make(h); Sleep(300);
    std::vector<float> bad(2000, 0.1f); bad[5]=NAN; bad[7]=INFINITY; bad[9]=-INFINITY;
    auto y=Run(e,bad,256); bool fin=true; for(float v:y) if(!std::isfinite(v)) fin=false;
    auto y2=Run(e,sine,256); bool fin2=true; for(float v:y2) if(!std::isfinite(v)) fin2=false;
    CHECK(fin&&fin2&&Peak(y2)>0.05f,"NaN/Inf input is dropped, output stays finite and alive afterwards");
    char buf[64]; e->dispatcher(e,8,-1,0,buf,0); e->dispatcher(e,8,9999,0,buf,0); e->dispatcher(e,6,-5,0,buf,0); e->dispatcher(e,7,12345,0,buf,0); e->dispatcher(e,8,3,0,nullptr,0);
    CHECK(true,"invalid parameter indices / null buffer do not crash");
    e->dispatcher(e,0,0,0,0,0);   // a second effOpen must be harmless (no second worker thread, no terminate)
    e->dispatcher(e,12,0,1,0,0); e->dispatcher(e,12,0,0,0,0); e->dispatcher(e,12,0,1,0,0);
    auto y3=Run(e,sine,256); CHECK(std::isfinite(Peak(y3))&&Peak(y3)>0.05f,"suspend/resume keeps working");
    e->dispatcher(e,1,0,0,0,0); }
  // 11. soak: random block sizes, random signals (incl. NaN/Inf/denormals), random and broken settings, instance churn.
  //     Detects crashes, non-finite/over-range output, and leaks of handles or memory. Length: --soak <seconds> (default 8).
  { std::mt19937 rng(42); LARGE_INTEGER freq, a, b; QueryPerformanceFrequency(&freq);
    auto privateBytes=[](){ PROCESS_MEMORY_COUNTERS_EX m{}; GetProcessMemoryInfo(GetCurrentProcess(),(PROCESS_MEMORY_COUNTERS*)&m,sizeof m); return (size_t)m.PrivateUsage; };
    auto handles=[](){ DWORD n=0; GetProcessHandleCount(GetCurrentProcess(),&n); return (int)n; };
    auto warm=[&](){ AEffect*w=Make(h); Sleep(150); Run(w,sine,512); w->dispatcher(w,1,0,0,0,0); };
    warm(); warm();                                   // warm-up: first use allocates caches
    int h0=handles(); size_t m0=privateBytes();
    auto json=[&](int kind)->std::string{
      char j[400];
      if(kind==0) return "{";                         // broken
      if(kind==1) return "";
      if(kind==2) return "{\"Settings\":{\"ThresholdDb\":nan,\"Ratio\":inf,\"MakeupGainDb\":-inf}}";
      auto U=[&](double lo,double hi){ return lo+(hi-lo)*(rng()/4294967296.0); };
      sprintf(j,"{\"Settings\":{\"Enabled\":%s,\"ThresholdDb\":%.2f,\"Ratio\":%.2f,\"AttackMs\":%.2f,\"ReleaseMs\":%.1f,\"MakeupGainDb\":%.2f,\"SpeechBoostDb\":%.2f,\"SpeechFreqHz\":%.0f,\"KneeDb\":%.2f}}",
        (rng()%6==0)?"false":"true",U(-80,10),U(0.5,30),U(0,300),U(1,3000),U(-40,12),U(-1,10),U(500,9000),U(-2,30));
      return j; };
    bool ok=true; long long blocks=0, frames=0, instances=1; double processSeconds=0;
    AEffect*e=Make(h); DWORD t0=GetTickCount(), nextSwap=t0+1000, nextSettings=t0;
    std::vector<float> in, outL, outR;
    while(GetTickCount()-t0 < soakSeconds*1000){
      DWORD now=GetTickCount();
      if(now>=nextSettings){ WriteSettings(json((int)(rng()%12)).c_str()); nextSettings=now+50+rng()%150; }
      if(now>=nextSwap){ e->dispatcher(e,1,0,0,0,0); e=Make(h); ++instances; nextSwap=now+1000; }
      int n=1+(int)(rng()%2048); in.assign(n,0.f); outL.assign(n,0.f); outR.assign(n,0.f);
      switch(rng()%8){
        case 0: for(int i=0;i<n;i++) in[i]=0.9f*sinf(0.05f*(float)(i+blocks)); break;
        case 1: for(int i=0;i<n;i++) in[i]=((rng()%20001)/10000.f-1.f); break;
        case 2: break;                                                    // silence
        case 3: for(int i=0;i<n;i++) in[i]=(i/8%2)?1.f:-1.f; break;        // full-scale square
        case 4: in[0]=1.f; break;                                          // impulse
        case 5: for(int i=0;i<n;i++) in[i]=0.3f; in[rng()%n]=NAN; in[rng()%n]=INFINITY; in[rng()%n]=-INFINITY; break;
        case 6: for(int i=0;i<n;i++) in[i]=1e-30f*(float)((int)(rng()%3)-1); break;   // denormal territory
        default: for(int i=0;i<n;i++) in[i]=0.05f*sinf(0.2f*(float)i); break;
      }
      float*pin[2]={in.data(),in.data()},*pout[2]={outL.data(),outR.data()};
      QueryPerformanceCounter(&a); e->pr(e,pin,pout,n); QueryPerformanceCounter(&b);
      processSeconds+=(double)(b.QuadPart-a.QuadPart)/freq.QuadPart; ++blocks; frames+=n;
      for(int i=0;i<n;i++) if(!std::isfinite(outL[i])||!std::isfinite(outR[i])||fabsf(outL[i])>1.0001f||fabsf(outR[i])>1.0001f){ ok=false; }
      if((blocks&7)==0) Sleep(1);
    }
    e->dispatcher(e,1,0,0,0,0); Sleep(400);
    int h1=handles(); size_t m1=privateBytes();
    CHECK(ok,"soak %.0f s: %lld blocks, %lld instances, all output finite and within +-1.0", soakSeconds, blocks, instances);
    CHECK(h1-h0<=2,"soak: no handle leak (%d -> %d)",h0,h1);
    CHECK((long long)m1-(long long)m0 < 8*1024*1024,"soak: no memory leak (%.1f MB -> %.1f MB)",m0/1048576.0,m1/1048576.0);
    double audio=(double)frames/48000.0; CHECK(processSeconds < audio/50.0,"soak: CPU cost %.2f%% of one core at 48 kHz stereo (%.0fx real time)",100.0*processSeconds/audio,audio/processSeconds); }
  // 8. status file: plug-in reports processed blocks once a second
  { WriteSettings("{\"Settings\":{\"Enabled\":true,\"ThresholdDb\":-30,\"Ratio\":4}}"); AEffect*e=Make(h); Sleep(300); Run(e,sine,512); Sleep(1500);
    WIN32_FIND_DATAW fd; std::wstring pat=path.substr(0,path.rfind(L'/'))+L"/meter-*.txt"; HANDLE f=FindFirstFileW(pat.c_str(),&fd);
    bool found=f!=INVALID_HANDLE_VALUE; std::string txt; if(found){ std::wstring full=path.substr(0,path.rfind(L'/'))+L"/"+fd.cFileName; HANDLE r=CreateFileW(full.c_str(),GENERIC_READ,7,0,OPEN_EXISTING,0,0); char b[256]={0}; DWORD g; ReadFile(r,b,255,&g,0); CloseHandle(r); txt=b; FindClose(f);}
    CHECK(found&&txt.find("tick=")!=std::string::npos&&txt.find("grDb")!=std::string::npos&&txt.find("blocks=")!=std::string::npos,"meter file written: %s",txt.c_str());
    auto count=[&](){ int n=0; WIN32_FIND_DATAW d; HANDLE h2=FindFirstFileW(pat.c_str(),&d); if(h2!=INVALID_HANDLE_VALUE){ do n++; while(FindNextFileW(h2,&d)); FindClose(h2);} return n; };
    int before=count(); e->dispatcher(e,1,0,0,0,0); Sleep(300); int after=count(); CHECK(after==before-1,"meter file removed on close (%d -> %d files)",before,after); }
  DeleteFileW(path.c_str());
  printf(fails?"\n%d FAILED\n":"\nALL PLUGIN TESTS PASSED\n",fails); return fails; }
