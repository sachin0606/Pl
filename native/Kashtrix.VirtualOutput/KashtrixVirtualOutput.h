#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <dshow.h>
#include <streams.h>

EXTERN_C const CLSID CLSID_KashtrixVirtualOutput;

class CKashtrixVirtualStream final : public CSourceStream, public IAMStreamConfig
{
public:
    CKashtrixVirtualStream(HRESULT* phr, CSource* parent);
    ~CKashtrixVirtualStream() override;
    DECLARE_IUNKNOWN
    STDMETHODIMP NonDelegatingQueryInterface(REFIID riid, void** ppv) override;
    HRESULT GetMediaType(CMediaType* mediaType) override;
    HRESULT CheckMediaType(const CMediaType* mediaType) override;
    HRESULT DecideBufferSize(IMemAllocator* allocator, ALLOCATOR_PROPERTIES* properties) override;
    HRESULT FillBuffer(IMediaSample* sample) override;
    STDMETHODIMP SetFormat(AM_MEDIA_TYPE* mediaType) override;
    STDMETHODIMP GetFormat(AM_MEDIA_TYPE** mediaType) override;
    STDMETHODIMP GetNumberOfCapabilities(int* count, int* size) override;
    STDMETHODIMP GetStreamCaps(int index, AM_MEDIA_TYPE** mediaType, BYTE* caps) override;
private:
    struct Header { LONG magic; LONG version; LONG width; LONG height; LONG stride; double fps; LONGLONG sequence; LONGLONG utcTicks; LONG bytes; };
    bool EnsureMapping(); bool ReadHeader(Header& header) const; void RefreshFormatFromBridge(); void CloseMapping(); REFERENCE_TIME FrameDuration() const;
    HANDLE _map=nullptr; BYTE* _view=nullptr; SIZE_T _mappedBytes=0; LONG _width=1920; LONG _height=1080; double _fps=50.0; LONGLONG _lastSequence=-1; REFERENCE_TIME _nextTime=0;
};

class CKashtrixVirtualAudioStream final : public CSourceStream, public IAMStreamConfig
{
public:
    CKashtrixVirtualAudioStream(HRESULT* phr, CSource* parent);
    ~CKashtrixVirtualAudioStream() override;
    DECLARE_IUNKNOWN
    STDMETHODIMP NonDelegatingQueryInterface(REFIID riid, void** ppv) override;
    HRESULT GetMediaType(CMediaType* mediaType) override;
    HRESULT CheckMediaType(const CMediaType* mediaType) override;
    HRESULT DecideBufferSize(IMemAllocator* allocator, ALLOCATOR_PROPERTIES* properties) override;
    HRESULT FillBuffer(IMediaSample* sample) override;
    STDMETHODIMP SetFormat(AM_MEDIA_TYPE* mediaType) override;
    STDMETHODIMP GetFormat(AM_MEDIA_TYPE** mediaType) override;
    STDMETHODIMP GetNumberOfCapabilities(int* count, int* size) override;
    STDMETHODIMP GetStreamCaps(int index, AM_MEDIA_TYPE** mediaType, BYTE* caps) override;
private:
    struct AudioHeader { LONG magic; LONG version; LONG sampleRate; LONG channels; LONG bits; LONGLONG sequence; LONGLONG utcTicks; LONG bytes; };
    bool EnsureMapping(); bool ReadHeader(AudioHeader& header) const; void RefreshFormatFromBridge(); void CloseMapping();
    HANDLE _map=nullptr; BYTE* _view=nullptr; SIZE_T _mappedBytes=0; LONG _sampleRate=48000; LONG _channels=2; LONG _bits=16; LONGLONG _lastSequence=-1; REFERENCE_TIME _nextTime=0;
};

class CKashtrixVirtualOutput final : public CSource
{
public: static CUnknown* WINAPI CreateInstance(IUnknown* outer, HRESULT* phr);
private: CKashtrixVirtualOutput(IUnknown* outer, HRESULT* phr);
};
