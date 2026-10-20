#include "KashtrixVirtualOutput.h"
#include <algorithm>
#include <cmath>
#include <climits>

EXTERN_C const CLSID CLSID_KashtrixVirtualOutput =
{ 0xc7453623, 0xa2e2, 0x49e0, { 0xb6, 0x79, 0x8b, 0xc4, 0xb5, 0x2d, 0x57, 0xd2 } };

namespace
{
    constexpr LONG KTXV_MAGIC=0x4B545856, KTXA_MAGIC=0x4B545841;
    constexpr SIZE_T HEADER_BYTES=256;
    constexpr wchar_t VIDEO_MAP[]=L"KashtrixPlayout.VirtualOutput.Video.v1";
    constexpr wchar_t AUDIO_MAP[]=L"KashtrixPlayout.VirtualOutput.Audio.v1";
    wchar_t PROGRAM_VIDEO_PIN_NAME[]=L"Program Video";
    wchar_t PROGRAM_AUDIO_PIN_NAME[]=L"Program Audio";
    wchar_t FILTER_FRIENDLY_NAME[]=L"Kashtrix Playout Virtual Output";

    void BuildVideoInfo(CMediaType& mt,LONG width,LONG height,double fps)
    {
        mt.InitMediaType(); mt.SetType(&MEDIATYPE_Video); mt.SetSubtype(&MEDIASUBTYPE_RGB32); mt.SetFormatType(&FORMAT_VideoInfo); mt.SetTemporalCompression(FALSE); mt.SetSampleSize(width*height*4);
        auto* vih=reinterpret_cast<VIDEOINFOHEADER*>(mt.AllocFormatBuffer(sizeof(VIDEOINFOHEADER))); ZeroMemory(vih,sizeof(*vih));
        vih->AvgTimePerFrame=static_cast<REFERENCE_TIME>(10000000.0/std::max(1.0,fps)); vih->bmiHeader.biSize=sizeof(BITMAPINFOHEADER); vih->bmiHeader.biWidth=width; vih->bmiHeader.biHeight=-height; vih->bmiHeader.biPlanes=1; vih->bmiHeader.biBitCount=32; vih->bmiHeader.biCompression=BI_RGB; vih->bmiHeader.biSizeImage=width*height*4; SetRectEmpty(&vih->rcSource); SetRectEmpty(&vih->rcTarget);
    }
    void BuildAudioInfo(CMediaType& mt,LONG rate,LONG channels,LONG bits)
    {
        mt.InitMediaType(); mt.SetType(&MEDIATYPE_Audio); mt.SetSubtype(&MEDIASUBTYPE_PCM); mt.SetFormatType(&FORMAT_WaveFormatEx); mt.SetTemporalCompression(FALSE);
        auto* wf=reinterpret_cast<WAVEFORMATEX*>(mt.AllocFormatBuffer(sizeof(WAVEFORMATEX))); ZeroMemory(wf,sizeof(*wf));
        wf->wFormatTag=WAVE_FORMAT_PCM; wf->nChannels=static_cast<WORD>(channels); wf->nSamplesPerSec=rate; wf->wBitsPerSample=static_cast<WORD>(bits); wf->nBlockAlign=static_cast<WORD>(channels*bits/8); wf->nAvgBytesPerSec=rate*wf->nBlockAlign; wf->cbSize=0; mt.SetSampleSize(wf->nBlockAlign);
    }
}

CKashtrixVirtualStream::CKashtrixVirtualStream(HRESULT* phr,CSource* parent):CSourceStream(NAME("Kashtrix Playout Virtual Video"),phr,parent,L"Program Video"){RefreshFormatFromBridge();}
CKashtrixVirtualStream::~CKashtrixVirtualStream(){CloseMapping();}
STDMETHODIMP CKashtrixVirtualStream::NonDelegatingQueryInterface(REFIID riid,void** ppv){if(riid==IID_IAMStreamConfig)return GetInterface(static_cast<IAMStreamConfig*>(this),ppv);return CSourceStream::NonDelegatingQueryInterface(riid,ppv);}
bool CKashtrixVirtualStream::EnsureMapping(){if(_view)return true;_map=OpenFileMappingW(FILE_MAP_READ,FALSE,VIDEO_MAP);if(!_map)return false;_view=static_cast<BYTE*>(MapViewOfFile(_map,FILE_MAP_READ,0,0,0));if(!_view){CloseHandle(_map);_map=nullptr;return false;}MEMORY_BASIC_INFORMATION mbi{};if(VirtualQuery(_view,&mbi,sizeof(mbi))!=0)_mappedBytes=mbi.RegionSize;return true;}
void CKashtrixVirtualStream::CloseMapping(){if(_view){UnmapViewOfFile(_view);_view=nullptr;}if(_map){CloseHandle(_map);_map=nullptr;}_mappedBytes=0;}
bool CKashtrixVirtualStream::ReadHeader(Header& h)const{if(!_view)return false;h.magic=*reinterpret_cast<volatile LONG*>(_view+0);h.version=*reinterpret_cast<volatile LONG*>(_view+4);h.width=*reinterpret_cast<volatile LONG*>(_view+8);h.height=*reinterpret_cast<volatile LONG*>(_view+12);h.stride=*reinterpret_cast<volatile LONG*>(_view+16);h.fps=*reinterpret_cast<volatile double*>(_view+20);h.sequence=*reinterpret_cast<volatile LONGLONG*>(_view+28);h.utcTicks=*reinterpret_cast<volatile LONGLONG*>(_view+36);h.bytes=*reinterpret_cast<volatile LONG*>(_view+44);return h.magic==KTXV_MAGIC&&h.version==1&&h.width>0&&h.height>0&&h.bytes>0;}
void CKashtrixVirtualStream::RefreshFormatFromBridge(){if(!EnsureMapping())return;Header h{};if(!ReadHeader(h))return;_width=std::clamp(h.width,320L,7680L);_height=std::clamp(h.height,240L,4320L);_fps=std::clamp(h.fps,1.0,120.0);}
REFERENCE_TIME CKashtrixVirtualStream::FrameDuration()const{return static_cast<REFERENCE_TIME>(10000000.0/std::max(1.0,_fps));}
HRESULT CKashtrixVirtualStream::GetMediaType(CMediaType* mt){if(!mt)return E_POINTER;RefreshFormatFromBridge();BuildVideoInfo(*mt,_width,_height,_fps);return S_OK;}
HRESULT CKashtrixVirtualStream::CheckMediaType(const CMediaType* mt){if(!mt||*mt->Type()!=MEDIATYPE_Video||*mt->Subtype()!=MEDIASUBTYPE_RGB32||*mt->FormatType()!=FORMAT_VideoInfo)return E_INVALIDARG;const auto* vih=reinterpret_cast<const VIDEOINFOHEADER*>(mt->Format());return vih&&vih->bmiHeader.biWidth==_width&&std::abs(vih->bmiHeader.biHeight)==_height?S_OK:E_INVALIDARG;}
HRESULT CKashtrixVirtualStream::DecideBufferSize(IMemAllocator* allocator,ALLOCATOR_PROPERTIES* props){if(!allocator||!props)return E_POINTER;RefreshFormatFromBridge();props->cBuffers=std::max<LONG>(props->cBuffers,3);props->cbBuffer=std::max<LONG>(props->cbBuffer,_width*_height*4);ALLOCATOR_PROPERTIES actual{};HRESULT hr=allocator->SetProperties(props,&actual);if(FAILED(hr))return hr;return actual.cbBuffer>=_width*_height*4?S_OK:E_FAIL;}
HRESULT CKashtrixVirtualStream::FillBuffer(IMediaSample* sample){if(!sample)return E_POINTER;BYTE* dst=nullptr;HRESULT hr=sample->GetPointer(&dst);if(FAILED(hr)||!dst)return FAILED(hr)?hr:E_POINTER;const LONG required=_width*_height*4,target=std::min(required,sample->GetSize());bool copied=false;if(EnsureMapping()){Header h{};if(ReadHeader(h)&&h.width==_width&&h.height==_height&&h.bytes>=required&&_mappedBytes>=HEADER_BYTES+static_cast<SIZE_T>(required)){auto seq=h.sequence;CopyMemory(dst,_view+HEADER_BYTES,target);MemoryBarrier();Header check{};if(ReadHeader(check)&&check.sequence==seq){_lastSequence=seq;copied=true;}}}if(!copied)ZeroMemory(dst,target);sample->SetActualDataLength(target);sample->SetSyncPoint(TRUE);auto duration=FrameDuration();REFERENCE_TIME start=_nextTime,stop=start+duration;sample->SetTime(&start,&stop);_nextTime=stop;return S_OK;}
STDMETHODIMP CKashtrixVirtualStream::SetFormat(AM_MEDIA_TYPE* m){if(!m)return E_POINTER;CMediaType mt(*m);return CheckMediaType(&mt);} STDMETHODIMP CKashtrixVirtualStream::GetFormat(AM_MEDIA_TYPE** m){if(!m)return E_POINTER;*m=nullptr;CMediaType mt;HRESULT hr=GetMediaType(&mt);if(FAILED(hr))return hr;*m=CreateMediaType(&mt);return *m?S_OK:E_OUTOFMEMORY;} STDMETHODIMP CKashtrixVirtualStream::GetNumberOfCapabilities(int* c,int* s){if(!c||!s)return E_POINTER;*c=1;*s=sizeof(VIDEO_STREAM_CONFIG_CAPS);return S_OK;} STDMETHODIMP CKashtrixVirtualStream::GetStreamCaps(int i,AM_MEDIA_TYPE** m,BYTE* caps){if(i!=0)return S_FALSE;if(!m||!caps)return E_POINTER;HRESULT hr=GetFormat(m);if(FAILED(hr))return hr;auto* c=reinterpret_cast<VIDEO_STREAM_CONFIG_CAPS*>(caps);ZeroMemory(c,sizeof(*c));c->guid=FORMAT_VideoInfo;c->InputSize.cx=c->MinOutputSize.cx=c->MaxOutputSize.cx=_width;c->InputSize.cy=c->MinOutputSize.cy=c->MaxOutputSize.cy=_height;c->MinFrameInterval=c->MaxFrameInterval=FrameDuration();auto bits=static_cast<long long>(_width)*_height*32LL*static_cast<long long>(std::ceil(_fps));c->MinBitsPerSecond=c->MaxBitsPerSecond=static_cast<LONG>(std::min<long long>(LONG_MAX,bits));return S_OK;}

CKashtrixVirtualAudioStream::CKashtrixVirtualAudioStream(HRESULT* phr,CSource* parent):CSourceStream(NAME("Kashtrix Playout Virtual Audio"),phr,parent,L"Program Audio"){RefreshFormatFromBridge();}
CKashtrixVirtualAudioStream::~CKashtrixVirtualAudioStream(){CloseMapping();}
STDMETHODIMP CKashtrixVirtualAudioStream::NonDelegatingQueryInterface(REFIID riid,void** ppv){if(riid==IID_IAMStreamConfig)return GetInterface(static_cast<IAMStreamConfig*>(this),ppv);return CSourceStream::NonDelegatingQueryInterface(riid,ppv);}
bool CKashtrixVirtualAudioStream::EnsureMapping(){if(_view)return true;_map=OpenFileMappingW(FILE_MAP_READ,FALSE,AUDIO_MAP);if(!_map)return false;_view=static_cast<BYTE*>(MapViewOfFile(_map,FILE_MAP_READ,0,0,0));if(!_view){CloseHandle(_map);_map=nullptr;return false;}MEMORY_BASIC_INFORMATION mbi{};if(VirtualQuery(_view,&mbi,sizeof(mbi))!=0)_mappedBytes=mbi.RegionSize;return true;}
void CKashtrixVirtualAudioStream::CloseMapping(){if(_view){UnmapViewOfFile(_view);_view=nullptr;}if(_map){CloseHandle(_map);_map=nullptr;}_mappedBytes=0;}
bool CKashtrixVirtualAudioStream::ReadHeader(AudioHeader& h)const{if(!_view)return false;h.magic=*reinterpret_cast<volatile LONG*>(_view+0);h.version=*reinterpret_cast<volatile LONG*>(_view+4);h.sampleRate=*reinterpret_cast<volatile LONG*>(_view+8);h.channels=*reinterpret_cast<volatile LONG*>(_view+12);h.bits=*reinterpret_cast<volatile LONG*>(_view+16);h.sequence=*reinterpret_cast<volatile LONGLONG*>(_view+20);h.utcTicks=*reinterpret_cast<volatile LONGLONG*>(_view+28);h.bytes=*reinterpret_cast<volatile LONG*>(_view+36);return h.magic==KTXA_MAGIC&&h.version==1&&h.sampleRate>0&&h.channels>0&&h.bits>0&&h.bytes>=0;}
void CKashtrixVirtualAudioStream::RefreshFormatFromBridge(){if(!EnsureMapping())return;AudioHeader h{};if(!ReadHeader(h))return;_sampleRate=std::clamp(h.sampleRate,8000L,192000L);_channels=std::clamp(h.channels,1L,32L);_bits=(h.bits==16||h.bits==24||h.bits==32)?h.bits:16;}
HRESULT CKashtrixVirtualAudioStream::GetMediaType(CMediaType* mt){if(!mt)return E_POINTER;RefreshFormatFromBridge();BuildAudioInfo(*mt,_sampleRate,_channels,_bits);return S_OK;}
HRESULT CKashtrixVirtualAudioStream::CheckMediaType(const CMediaType* mt){if(!mt||*mt->Type()!=MEDIATYPE_Audio||*mt->Subtype()!=MEDIASUBTYPE_PCM||*mt->FormatType()!=FORMAT_WaveFormatEx)return E_INVALIDARG;const auto* wf=reinterpret_cast<const WAVEFORMATEX*>(mt->Format());return wf&&wf->nSamplesPerSec==static_cast<DWORD>(_sampleRate)&&wf->nChannels==_channels&&wf->wBitsPerSample==_bits?S_OK:E_INVALIDARG;}
HRESULT CKashtrixVirtualAudioStream::DecideBufferSize(IMemAllocator* allocator,ALLOCATOR_PROPERTIES* props){if(!allocator||!props)return E_POINTER;RefreshFormatFromBridge();LONG oneSecond=_sampleRate*_channels*_bits/8;props->cBuffers=std::max<LONG>(props->cBuffers,4);props->cbBuffer=std::max<LONG>(props->cbBuffer,oneSecond);ALLOCATOR_PROPERTIES actual{};HRESULT hr=allocator->SetProperties(props,&actual);if(FAILED(hr))return hr;return actual.cbBuffer>=oneSecond?S_OK:E_FAIL;}
HRESULT CKashtrixVirtualAudioStream::FillBuffer(IMediaSample* sample){if(!sample)return E_POINTER;BYTE* dst=nullptr;HRESULT hr=sample->GetPointer(&dst);if(FAILED(hr)||!dst)return FAILED(hr)?hr:E_POINTER;LONG bytes=0;bool copied=false;if(EnsureMapping()){AudioHeader h{};if(ReadHeader(h)&&h.sampleRate==_sampleRate&&h.channels==_channels&&h.bits==_bits&&h.bytes>0&&_mappedBytes>=HEADER_BYTES+static_cast<SIZE_T>(h.bytes)){bytes=std::min<LONG>(h.bytes,sample->GetSize());auto seq=h.sequence;CopyMemory(dst,_view+HEADER_BYTES,bytes);MemoryBarrier();AudioHeader check{};if(ReadHeader(check)&&check.sequence==seq){_lastSequence=seq;copied=true;}}}if(!copied){bytes=std::min<LONG>(sample->GetSize(),_sampleRate*_channels*_bits/8/50);ZeroMemory(dst,bytes);}sample->SetActualDataLength(bytes);sample->SetSyncPoint(TRUE);const LONG block=std::max<LONG>(1,_channels*_bits/8);const LONGLONG frames=bytes/block;const REFERENCE_TIME duration=static_cast<REFERENCE_TIME>((frames*10000000LL)/std::max<LONG>(1,_sampleRate));REFERENCE_TIME start=_nextTime,stop=start+std::max<REFERENCE_TIME>(1,duration);sample->SetTime(&start,&stop);_nextTime=stop;return S_OK;}
STDMETHODIMP CKashtrixVirtualAudioStream::SetFormat(AM_MEDIA_TYPE* m){if(!m)return E_POINTER;CMediaType mt(*m);return CheckMediaType(&mt);} STDMETHODIMP CKashtrixVirtualAudioStream::GetFormat(AM_MEDIA_TYPE** m){if(!m)return E_POINTER;*m=nullptr;CMediaType mt;HRESULT hr=GetMediaType(&mt);if(FAILED(hr))return hr;*m=CreateMediaType(&mt);return *m?S_OK:E_OUTOFMEMORY;} STDMETHODIMP CKashtrixVirtualAudioStream::GetNumberOfCapabilities(int* c,int* s){if(!c||!s)return E_POINTER;*c=1;*s=sizeof(AUDIO_STREAM_CONFIG_CAPS);return S_OK;} STDMETHODIMP CKashtrixVirtualAudioStream::GetStreamCaps(int i,AM_MEDIA_TYPE** m,BYTE* caps){if(i!=0)return S_FALSE;if(!m||!caps)return E_POINTER;HRESULT hr=GetFormat(m);if(FAILED(hr))return hr;auto* c=reinterpret_cast<AUDIO_STREAM_CONFIG_CAPS*>(caps);ZeroMemory(c,sizeof(*c));c->guid=FORMAT_WaveFormatEx;c->MinimumChannels=c->MaximumChannels=_channels;c->ChannelsGranularity=1;c->MinimumBitsPerSample=c->MaximumBitsPerSample=_bits;c->BitsPerSampleGranularity=1;c->MinimumSampleFrequency=c->MaximumSampleFrequency=_sampleRate;c->SampleFrequencyGranularity=1;return S_OK;}

CUnknown* WINAPI CKashtrixVirtualOutput::CreateInstance(IUnknown* outer,HRESULT* phr){auto* source=new CKashtrixVirtualOutput(outer,phr);if(!source&&phr)*phr=E_OUTOFMEMORY;return source;}
CKashtrixVirtualOutput::CKashtrixVirtualOutput(IUnknown* outer,HRESULT* phr):CSource(NAME("Kashtrix Playout Virtual Output"),outer,CLSID_KashtrixVirtualOutput){new CKashtrixVirtualStream(phr,this);new CKashtrixVirtualAudioStream(phr,this);}

static const AMOVIESETUP_MEDIATYPE s_videoPinTypes[]={{&MEDIATYPE_Video,&MEDIASUBTYPE_RGB32}};
static const AMOVIESETUP_MEDIATYPE s_audioPinTypes[]={{&MEDIATYPE_Audio,&MEDIASUBTYPE_PCM}};
static const AMOVIESETUP_PIN s_pins[]={
 {PROGRAM_VIDEO_PIN_NAME,FALSE,TRUE,FALSE,FALSE,&CLSID_NULL,nullptr,1,s_videoPinTypes},
 {PROGRAM_AUDIO_PIN_NAME,FALSE,TRUE,FALSE,FALSE,&CLSID_NULL,nullptr,1,s_audioPinTypes}
};
static const AMOVIESETUP_FILTER s_filter={&CLSID_KashtrixVirtualOutput,FILTER_FRIENDLY_NAME,MERIT_DO_NOT_USE,2,s_pins};
CFactoryTemplate g_Templates[]={{FILTER_FRIENDLY_NAME,&CLSID_KashtrixVirtualOutput,CKashtrixVirtualOutput::CreateInstance,nullptr,&s_filter}}; int g_cTemplates=sizeof(g_Templates)/sizeof(g_Templates[0]);

static HRESULT RegisterCategory(const CLSID& category,bool add,REGPINTYPES* types,ULONG count)
{
 HRESULT init=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);IFilterMapper2* mapper=nullptr;HRESULT hr=CoCreateInstance(CLSID_FilterMapper2,nullptr,CLSCTX_INPROC_SERVER,IID_IFilterMapper2,reinterpret_cast<void**>(&mapper));
 if(SUCCEEDED(hr)&&mapper){if(add){REGFILTERPINS2 pin{};pin.dwFlags=REG_PINFLAG_B_OUTPUT;pin.cInstances=1;pin.nMediaTypes=count;pin.lpMediaType=types;pin.clsPinCategory=&PIN_CATEGORY_CAPTURE;REGFILTER2 f{};f.dwVersion=2;f.dwMerit=MERIT_NORMAL;f.cPins=1;f.rgPins2=&pin;hr=mapper->RegisterFilter(CLSID_KashtrixVirtualOutput,FILTER_FRIENDLY_NAME,nullptr,&category,nullptr,&f);}else hr=mapper->UnregisterFilter(&category,nullptr,CLSID_KashtrixVirtualOutput);mapper->Release();}
 if(SUCCEEDED(init))CoUninitialize();return hr;
}
STDAPI DllRegisterServer(){HRESULT hr=AMovieDllRegisterServer2(TRUE);if(FAILED(hr))return hr;REGPINTYPES v{&MEDIATYPE_Video,&MEDIASUBTYPE_RGB32};REGPINTYPES a{&MEDIATYPE_Audio,&MEDIASUBTYPE_PCM};hr=RegisterCategory(CLSID_VideoInputDeviceCategory,true,&v,1);if(FAILED(hr))return hr;return RegisterCategory(CLSID_AudioInputDeviceCategory,true,&a,1);}
STDAPI DllUnregisterServer(){REGPINTYPES v{&MEDIATYPE_Video,&MEDIASUBTYPE_RGB32};REGPINTYPES a{&MEDIATYPE_Audio,&MEDIASUBTYPE_PCM};RegisterCategory(CLSID_VideoInputDeviceCategory,false,&v,1);RegisterCategory(CLSID_AudioInputDeviceCategory,false,&a,1);return AMovieDllRegisterServer2(FALSE);}
extern "C" BOOL WINAPI DllEntryPoint(HINSTANCE,ULONG,LPVOID); BOOL APIENTRY DllMain(HMODULE module,DWORD reason,LPVOID reserved){return DllEntryPoint(reinterpret_cast<HINSTANCE>(module),reason,reserved);}
