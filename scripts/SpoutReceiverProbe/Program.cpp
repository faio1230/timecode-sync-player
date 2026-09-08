// Diagnostic comparison derived from the retained probe-pixels.cpp.
// Native blocking Map requires an external process watchdog.
#define NOMINMAX
#include "SpoutDX.h"
#include <wrl/client.h>
#include <iomanip>
#include <sstream>
#include <array>
#include <stdexcept>
#include <atomic>
#include <memory>
#include <cmath>
using Microsoft::WRL::ComPtr;
static std::atomic<bool> stopped{false};
// Called only from this probe's generated ReceiveTexture() implementation on
// the receiving thread. Submission is distinct from successful Map completion.
static unsigned long long copySubmitCount=0;
extern "C" void SpoutProbeSharedCopySubmitted() { ++copySubmitCount; }
static BOOL WINAPI stop(DWORD event) { if(event==CTRL_C_EVENT || event==CTRL_BREAK_EVENT) {stopped=true; return TRUE;} return FALSE; }
static long long qpc() { LARGE_INTEGER x; QueryPerformanceCounter(&x); return x.QuadPart; }
static std::string quote(const std::string& s) { std::ostringstream o; o<<'"'; for(unsigned char c:s) {if(c=='"'||c=='\\')o<<'\\'<<c; else if(c<32)o<<"\\u"<<std::hex<<std::setw(4)<<std::setfill('0')<<(int)c<<std::dec; else o<<c;} o<<'"'; return o.str(); }
static int level(const unsigned char* p) { if(p[0]>=192&&p[1]>=192&&p[2]>=192)return 1; if(p[0]<=64&&p[1]<=64&&p[2]<=64)return 0;return -1; }
static std::string fingerprint(const unsigned char* p, UINT width, UINT height, UINT pitch, bool rgba, std::string* sampledBytes = nullptr) {
    uint64_t hash=14695981039346656037ull; if(sampledBytes)sampledBytes->clear(); const char* hex="0123456789abcdef";
    auto add=[&](int x,int row){const auto pixel=p+row*pitch+x*4;for(int channel=0;channel<3;channel++){const unsigned char value=pixel[rgba?2-channel:channel];hash^=value;hash*=1099511628211ull;if(sampledBytes){sampledBytes->push_back(hex[value>>4]);sampledBytes->push_back(hex[value&15]);}}};
    for(int row=0;row<9;row++)for(int col=0;col<9;col++)add((width-1)*col/8,row);
    if(width==1920&&height==1080)for(int bit=0;bit<48;bit++)add(40+bit*16,9);
    std::ostringstream out;out<<std::hex<<std::setw(16)<<std::setfill('0')<<hash;return out.str();
}
struct Marker {bool valid=false, black=false; int clip=-1, frame=-1;};
static Marker decode(const unsigned char* p, UINT width, UINT height, UINT pitch) {
    Marker m; m.black=true;
    for(int row=0;row<9;row++)for(int col=0;col<9;col++)m.black &= level(p+row*pitch+((width-1)*col/8)*4)==0;
    if(width!=1920||height!=1080)return m;
    std::array<unsigned char,6> bytes{}; bool valid=true;
    for(int bit=0;bit<48;bit++) {int value=level(p+9*pitch+(40+bit*16)*4);m.black &= value==0;if(value<0)valid=false;else bytes[bit/8] |= value<<(7-bit%8);}
    m.valid=valid&&bytes[0]==0xdd&&bytes[1]==0xaa&&bytes[4]>=1&&bytes[4]<=3&&(bytes[0]^bytes[1]^bytes[2]^bytes[3]^bytes[4])==bytes[5];
    if(m.valid){m.clip=bytes[4];m.frame=bytes[2]*256+bytes[3];m.black=false;}return m;
}
static int selftest(){std::vector<unsigned char> p(1920*10*4);unsigned char b[]={0xdd,0xaa,0x12,0x34,2,0x53};for(int bit=0;bit<48;bit++){int index=(9*1920+40+bit*16)*4;for(int c=0;c<3;c++)p[index+c]=(b[bit/8]&(1<<(7-bit%8)))?255:0;}auto m=decode(p.data(),1920,1080,1920*4);if(!m.valid||m.frame!=0x1234||m.clip!=2||m.black)return 1;if(fingerprint(p.data(),1920,1080,1920*4,false)!="0fd8651154d918f7")return 4;p[(9*1920+40)*4]=127;if(decode(p.data(),1920,1080,1920*4).valid)return 2;std::fill(p.begin(),p.end(),0);if(!decode(p.data(),1920,1080,1920*4).black)return 3;std::cout<<"marker and fingerprint self-test passed; sizeof(spoutDX)="<<sizeof(spoutDX)<<"; no DirectX device created\n";return 0;}
static unsigned long long filetime(FILETIME t){return (static_cast<unsigned long long>(t.dwHighDateTime)<<32)|t.dwLowDateTime;}
static double cpuSeconds(){FILETIME a,b,k,u;GetProcessTimes(GetCurrentProcess(),&a,&b,&k,&u);return (filetime(k)+filetime(u))/1e7;}

struct AccessGuard {
    HANDLE handle=nullptr;
    bool owned=false, completionKnown=true;
    double waitMs=0;
    explicit AccessGuard(const std::string& name, bool locked) {
        if(locked) {
            handle=CreateMutexA(nullptr,FALSE,(name+"_SpoutAccessMutex").c_str());
            if(!handle)throw std::runtime_error("CreateMutex failed");
        }
    }
    void acquire(long long frequency) {
        if(!handle)return;
        auto start=qpc();DWORD result=WaitForSingleObject(handle,100);
        waitMs=(qpc()-start)*1000.0/frequency;
        owned=result==WAIT_OBJECT_0||result==WAIT_ABANDONED;
        completionKnown=true; // No copy has been issued on this iteration yet.
        if(result==WAIT_ABANDONED) { completionKnown=false;throw std::runtime_error("Spout mutex abandoned; prior image completion unknown; observation rejected"); }
        if(result==WAIT_TIMEOUT)throw std::runtime_error("Spout mutex wait exceeded 100 ms; observation rejected");
        if(result!=WAIT_OBJECT_0)throw std::runtime_error("Spout mutex wait failed");
    }
    void release() {
        if(!owned)return;
        if(!completionKnown)throw std::runtime_error("Refusing normal unlock before GPU completion");
        if(!ReleaseMutex(handle))throw std::runtime_error("ReleaseMutex failed");
        owned=false;
    }
    ~AccessGuard() {
        // On an uncertain submitted-copy failure do not normally unlock or close
        // the owned handle: thread/process termination reports an abandoned mutex.
        // There is no retry or indefinite polling here. Native teardown/Map still
        // requires the external watchdog stated by the CLI and README.
        if(owned&&completionKnown) { if(ReleaseMutex(handle))owned=false; }
        if(handle&&!owned)CloseHandle(handle);
    }
};

struct SenderInfo {
    unsigned int width=0,height=0;
    HANDLE handle=nullptr;
    DWORD format=0;
    bool read(spoutDX& receiver,const std::string& name) {
        return receiver.GetSenderInfo(name.c_str(),width,height,handle,format)&&handle&&width&&height;
    }
    bool operator==(const SenderInfo& rhs)const {
        return width==rhs.width&&height==rhs.height&&handle==rhs.handle&&format==rhs.format;
    }
};

int main(int argc,char**argv) {
    if(argc==2&&std::string(argv[1])=="--self-test")return selftest();
    std::string sender,output,stopFile,mode;
    double duration=50;int pollMs=8;std::ofstream log;
    std::unique_ptr<AccessGuard> access;
    std::unique_ptr<spoutDX> receiverOwner;
    const auto started=qpc();LARGE_INTEGER frequency{};QueryPerformanceFrequency(&frequency);
    unsigned long long polls=0,samples=0,missedObservations=0;
    bool registered=false;SenderInfo initial;
    std::string stage="arguments";
    try {
        bool externalWatchdog=false;
        for(int i=1;i<argc;i++) {
            std::string option=argv[i];
            if(option=="--external-watchdog"){externalWatchdog=true;continue;}
            if(i+1>=argc)throw std::runtime_error("Missing option value");
            std::string value=argv[++i];
            if(option=="--sender")sender=value;
            else if(option=="--output")output=value;
            else if(option=="--stop-file")stopFile=value;
            else if(option=="--mode")mode=value;
            else if(option=="--duration")duration=std::stod(value);
            else if(option=="--poll-ms")pollMs=std::stoi(value);
            else throw std::runtime_error("Unknown option "+option);
        }
        if(sender.empty()||sender.size()>220||sender.find_first_not_of("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-. ")!=std::string::npos||output.empty()||!externalWatchdog||(mode!="baseline"&&mode!="locked")||!std::isfinite(duration)||duration<=0||duration>120||pollMs<1||pollMs>1000)
            throw std::runtime_error("Usage: SpoutReceiverProbe --sender EXACT_ASCII_NAME --output NEW_PATH --mode baseline|locked --external-watchdog [--duration 50] [--poll-ms 8] [--stop-file PATH]");
        // Reserve the new evidence path without truncating another run's output.
        HANDLE reserved=CreateFileA(output.c_str(),GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);
        if(reserved==INVALID_HANDLE_VALUE)throw std::runtime_error("Cannot create new output file");
        CloseHandle(reserved);log.open(output);if(!log)throw std::runtime_error("Cannot open reserved output file");
        log<<std::boolalpha<<std::setprecision(16);
        log<<"{\"event\":\"start\",\"qpc\":"<<started<<",\"qpcFrequency\":"<<frequency.QuadPart<<",\"pid\":"<<GetCurrentProcessId()<<",\"sender\":"<<quote(sender)<<",\"mode\":"<<quote(mode)<<",\"pollMs\":"<<pollMs<<",\"spoutObjectSize\":"<<sizeof(spoutDX)<<",\"mapDeadlineGuaranteed\":false,\"externalWatchdogRequired\":true,\"transport\":\"ReceiveTexture shared-to-private copy then synchronous ten-row staging Map; sample every connected poll in both modes\"}\n";log.flush();
        SetConsoleCtrlHandler(stop,TRUE);
        access=std::make_unique<AccessGuard>(sender,mode=="locked");
        // The receiver and all D3D references are destroyed before the mutex guard.
        receiverOwner=std::make_unique<spoutDX>();auto& receiver=*receiverOwner;
        receiver.SetReceiverName(sender.c_str());receiver.OpenDirectX11();
        receiver.SetAdapterAuto(false); // Keep context identity fixed for this experiment.
        auto device=receiver.GetDX11Device();auto context=receiver.GetDX11Context();
        if(!device||!context)throw std::runtime_error("Receiver device/context unavailable");
        ComPtr<ID3D11Texture2D> staging;
        long lastCounter=-1;auto lastFlush=started;double receiveTotal=0,readbackTotal=0;
        while(!stopped&&(qpc()-started)/double(frequency.QuadPart)<duration) {
            if(!stopFile.empty()&&GetFileAttributesA(stopFile.c_str())!=INVALID_FILE_ATTRIBUTES)break;
            stage="WaitMutex";access->acquire(frequency.QuadPart);polls++;
            stage="SenderInfoBefore";SenderInfo beforeInfo;
            if(!beforeInfo.read(receiver,sender)) {
                access->release();missedObservations++;
                log<<"{\"event\":\"observation\",\"qpc\":"<<qpc()<<",\"accepted\":false,\"reason\":\"exact sender not registered\"}\n";
                if(registered)throw std::runtime_error("Previously observed sender disappeared");
                Sleep(pollMs);continue;
            }
            if(registered&&!(initial==beforeInfo))throw std::runtime_error("Sender metadata changed before receive");
            stage="ReceiveTexture";auto receiveStart=qpc();
            const auto copyCountBefore=copySubmitCount;
            access->completionKnown=false; // ReceiveTexture may submit a shared-image copy.
            bool received=receiver.ReceiveTexture();auto receiveEnd=qpc();
            bool updated=receiver.IsUpdated(),connected=receiver.IsConnected(),frameNew=receiver.IsFrameNew();
            auto texture=receiver.GetSenderTexture();long counter=receiver.GetSenderFrame();
            if(!received||!connected||!texture)throw std::runtime_error("ReceiveTexture did not provide a connected private image");
            if(receiver.GetDX11Device()!=device||receiver.GetDX11Context()!=context)throw std::runtime_error("Receiver device/context changed");
            D3D11_TEXTURE2D_DESC desc{};texture->GetDesc(&desc);
            bool rgba=desc.Format==DXGI_FORMAT_R8G8B8A8_UNORM||desc.Format==DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
            if((!rgba&&desc.Format!=DXGI_FORMAT_B8G8R8A8_UNORM&&desc.Format!=DXGI_FORMAT_B8G8R8X8_UNORM&&desc.Format!=DXGI_FORMAT_B8G8R8A8_UNORM_SRGB)||!desc.Width||!desc.Height||desc.SampleDesc.Count!=1)
                throw std::runtime_error("Unsupported received format/size/sample count");
            stage="CreateStaging";
            if(!staging) {
                D3D11_TEXTURE2D_DESC s{};s.Width=desc.Width;s.Height=10;s.MipLevels=1;s.ArraySize=1;s.Format=desc.Format;s.SampleDesc.Count=1;s.Usage=D3D11_USAGE_STAGING;s.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
                HRESULT hr=device->CreateTexture2D(&s,nullptr,&staging);if(FAILED(hr))throw std::runtime_error("Create staging failed HRESULT="+std::to_string(hr));
            } else if(desc.Width!=initial.width||desc.Height!=initial.height||DWORD(desc.Format)!=initial.format)throw std::runtime_error("Private texture metadata changed");
            stage="CopyAndMap";auto readStart=qpc();
            for(UINT row=0;row<10;row++) { UINT y=row<9?(desc.Height-1)*row/8:(desc.Height>48?48:0);D3D11_BOX box{0,y,0,desc.Width,y+1,1};context->CopySubresourceRegion(staging.Get(),0,0,row,0,texture,0,&box); }
            D3D11_MAPPED_SUBRESOURCE mapped{};HRESULT hr=context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped);auto mapEnd=qpc();
            if(FAILED(hr))throw std::runtime_error("Map failed HRESULT="+std::to_string(hr));
            // Successful blocking Map proves preceding copies on this context ended.
            // CPU decoding touches staging only. Release before any log formatting.
            access->completionKnown=true;
            stage="ValidateAfterMap";SenderInfo afterInfo;
            bool metadataOkay=afterInfo.read(receiver,sender)&&beforeInfo==afterInfo&&
                sender==receiver.GetSenderName()&&receiver.GetSenderHandle()==beforeInfo.handle&&
                desc.Width==beforeInfo.width&&desc.Height==beforeInfo.height&&DWORD(desc.Format)==beforeInfo.format;
            Marker marker;std::string sampledBytes,contentHash;
            try {
                if(metadataOkay) {
                    marker=decode(static_cast<unsigned char*>(mapped.pData),desc.Width,desc.Height,mapped.RowPitch);
                    contentHash=fingerprint(static_cast<unsigned char*>(mapped.pData),desc.Width,desc.Height,mapped.RowPitch,rgba,&sampledBytes);
                }
            } catch(...) {context->Unmap(staging.Get(),0);throw;}
            UINT rowPitch=mapped.RowPitch;context->Unmap(staging.Get(),0);
            stage="ReleaseMutex";access->release();auto observed=qpc();
            if(!metadataOkay)throw std::runtime_error("Exact sender or metadata changed during receive; observation rejected");
            initial=beforeInfo;registered=true;
            const auto submittedThisPoll=copySubmitCount-copyCountBefore;
            if(copySubmitCount==0) {
                ++missedObservations;
                log<<"{\"event\":\"observation\",\"accepted\":false,\"qpc\":"<<observed<<",\"reason\":\"private image has no confirmed shared-copy submission\",\"receiveStartQpc\":"<<receiveStart<<",\"receiveEndQpc\":"<<receiveEnd<<",\"readbackStartQpc\":"<<readStart<<",\"mapEndQpc\":"<<mapEnd<<",\"copySubmitCount\":"<<copySubmitCount<<",\"copySubmitsThisPoll\":"<<submittedThisPoll<<",\"newFrame\":"<<frameNew<<",\"sampleBgrHex\":"<<quote(sampledBytes)<<"}\n";
                if(!log)throw std::runtime_error("Output write failed");
                Sleep(pollMs);continue;
            }
            samples++;receiveTotal+=(receiveEnd-receiveStart)*1000.0/frequency.QuadPart;readbackTotal+=(observed-readStart)*1000.0/frequency.QuadPart;
            log<<"{\"event\":\"frame\",\"accepted\":true,\"copySubmitCount\":"<<copySubmitCount<<",\"copySubmitsThisPoll\":"<<submittedThisPoll<<",\"qpc\":"<<observed<<",\"receiveStartQpc\":"<<receiveStart<<",\"receiveEndQpc\":"<<receiveEnd<<",\"readbackStartQpc\":"<<readStart<<",\"mapEndQpc\":"<<mapEnd<<",\"width\":"<<desc.Width<<",\"height\":"<<desc.Height<<",\"newFrame\":"<<frameNew<<",\"updated\":"<<updated<<",\"counterChanged\":"<<(lastCounter!=counter)<<",\"senderFrame\":"<<counter<<",\"shareHandle\":"<<reinterpret_cast<uintptr_t>(beforeInfo.handle)<<",\"contentHash\":"<<quote(contentHash)<<",\"sampleBgrHex\":"<<quote(sampledBytes)<<",\"markerValid\":"<<marker.valid<<",\"clipId\":"<<(marker.valid?std::to_string(marker.clip):"null")<<",\"frameIndex\":"<<(marker.valid?std::to_string(marker.frame):"null")<<",\"isBlack\":"<<marker.black<<",\"mutexMs\":"<<access->waitMs<<",\"receiveMs\":"<<(receiveEnd-receiveStart)*1000.0/frequency.QuadPart<<",\"copyAndMapMs\":"<<(mapEnd-readStart)*1000.0/frequency.QuadPart<<",\"readbackMs\":"<<(observed-readStart)*1000.0/frequency.QuadPart<<",\"mappedBytes\":"<<rowPitch*10<<"}\n";
            lastCounter=counter;
            if(observed-lastFlush>=frequency.QuadPart){log.flush();lastFlush=observed;}
            if(!log)throw std::runtime_error("Output write failed");
            Sleep(pollMs);
        }
        stage="CleanupCheckpoint";
        const auto cleanupCheckpoint=qpc();
        log<<"{\"event\":\"cleanup-stage-start\",\"qpc\":"<<cleanupCheckpoint<<",\"note\":\"native cleanup follows this checkpoint flush\"}\n";
        const auto checkpointFlushStart=qpc();log.flush();const auto cleanupStart=qpc();
        if(!log)throw std::runtime_error("Cleanup checkpoint write failed");
        stage="Cleanup";staging.Reset();receiver.ReleaseReceiver();receiver.CloseDirectX11();receiverOwner.reset();
        const auto cleanupEnd=qpc();
        log<<"{\"event\":\"cleanup-stage-end\",\"qpc\":"<<cleanupEnd<<",\"nativeStartQpc\":"<<cleanupStart<<",\"nativeEndQpc\":"<<cleanupEnd<<",\"nativeCleanupMs\":"<<(cleanupEnd-cleanupStart)*1000.0/frequency.QuadPart<<",\"startCheckpointFlushMs\":"<<(cleanupStart-checkpointFlushStart)*1000.0/frequency.QuadPart<<",\"destructorCompleted\":true}\n";
        const auto endFlushStart=qpc();log.flush();const auto endFlushEnd=qpc();
        if(!log)throw std::runtime_error("Cleanup completion write failed");
        log<<"{\"event\":\"cleanup-log-flush\",\"qpc\":"<<endFlushEnd<<",\"startQpc\":"<<endFlushStart<<",\"endQpc\":"<<endFlushEnd<<",\"elapsedMs\":"<<(endFlushEnd-endFlushStart)*1000.0/frequency.QuadPart<<"}\n";
        log<<"{\"event\":\"summary\",\"errors\":0,\"droppedLogEvents\":0,\"qpc\":"<<qpc()<<",\"mode\":"<<quote(mode)<<",\"polls\":"<<polls<<",\"samples\":"<<samples<<",\"rejectedObservations\":"<<missedObservations<<",\"receiveTotalMs\":"<<receiveTotal<<",\"readbackTotalMs\":"<<readbackTotal<<"}\n";log.flush();
        return samples&&log?0:2;
    } catch(const std::exception& error) {
        if(log.is_open()) {log<<"{\"event\":\"error\",\"accepted\":false,\"qpc\":"<<qpc()<<",\"stage\":"<<quote(stage)<<",\"message\":"<<quote(error.what())<<",\"mutexOwned\":"<<(access&&access->owned)<<",\"gpuCompletionKnown\":"<<(!access||access->completionKnown)<<",\"normalUnlockSuppressed\":"<<(access&&access->owned&&!access->completionKnown)<<"}\n";log.flush();}
        std::cerr<<error.what()<<'\n';return 1;
    }
}
