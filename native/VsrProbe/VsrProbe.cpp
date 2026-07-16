// Minimal NVIDIA RTX VSR driver-extension probe.
// Uses only Windows SDK interfaces; no NVIDIA SDK headers or binaries.

#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

#include <iomanip>
#include <iostream>

using Microsoft::WRL::ComPtr;

namespace
{
constexpr UINT NvidiaVendorId = 0x10DE;
constexpr GUID NvidiaPpeInterfaceGuid = {
    0xd43ce1b3, 0x1f4b, 0x48ac,
    {0xba, 0xee, 0xc3, 0xc2, 0x53, 0x75, 0xe6, 0xf7}
};

struct NvidiaVsrExtension
{
    UINT version = 1;
    UINT method = 2;
    UINT enable = 1;
};

void PrintResult(const wchar_t* operation, HRESULT result)
{
    std::wcout << operation << L": 0x"
               << std::hex << std::uppercase << std::setw(8) << std::setfill(L'0')
               << static_cast<unsigned long>(result) << std::dec
               << (SUCCEEDED(result) ? L" (success)" : L" (failed)") << L'\n';
}
}

int wmain()
{
    ComPtr<IDXGIFactory1> factory;
    HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
    if (FAILED(hr))
    {
        PrintResult(L"CreateDXGIFactory1", hr);
        return 1;
    }

    ComPtr<IDXGIAdapter1> nvidiaAdapter;
    DXGI_ADAPTER_DESC1 adapterDescription{};
    for (UINT index = 0;; ++index)
    {
        ComPtr<IDXGIAdapter1> candidate;
        if (factory->EnumAdapters1(index, &candidate) == DXGI_ERROR_NOT_FOUND)
            break;

        DXGI_ADAPTER_DESC1 description{};
        if (SUCCEEDED(candidate->GetDesc1(&description)) && description.VendorId == NvidiaVendorId)
        {
            nvidiaAdapter = candidate;
            adapterDescription = description;
            break;
        }
    }

    if (!nvidiaAdapter)
    {
        std::wcerr << L"No NVIDIA DXGI adapter was found.\n";
        return 2;
    }

    std::wcout << L"Adapter: " << adapterDescription.Description << L'\n';

    constexpr D3D_FEATURE_LEVEL levels[] = {
        D3D_FEATURE_LEVEL_12_1,
        D3D_FEATURE_LEVEL_12_0,
        D3D_FEATURE_LEVEL_11_1,
        D3D_FEATURE_LEVEL_11_0
    };

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    D3D_FEATURE_LEVEL selectedLevel{};
    hr = D3D11CreateDevice(
        nvidiaAdapter.Get(),
        D3D_DRIVER_TYPE_UNKNOWN,
        nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
        levels,
        ARRAYSIZE(levels),
        D3D11_SDK_VERSION,
        &device,
        &selectedLevel,
        &context);
    if (FAILED(hr))
    {
        PrintResult(L"D3D11CreateDevice", hr);
        return 3;
    }

    ComPtr<ID3D11VideoDevice> videoDevice;
    ComPtr<ID3D11VideoContext> videoContext;
    hr = device.As(&videoDevice);
    if (FAILED(hr))
    {
        PrintResult(L"Query ID3D11VideoDevice", hr);
        return 4;
    }
    hr = context.As(&videoContext);
    if (FAILED(hr))
    {
        PrintResult(L"Query ID3D11VideoContext", hr);
        return 5;
    }

    D3D11_VIDEO_PROCESSOR_CONTENT_DESC content{};
    content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
    content.InputFrameRate = {60, 1};
    content.InputWidth = 1280;
    content.InputHeight = 720;
    content.OutputFrameRate = {60, 1};
    content.OutputWidth = 1920;
    content.OutputHeight = 1080;
    content.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;

    ComPtr<ID3D11VideoProcessorEnumerator> enumerator;
    hr = videoDevice->CreateVideoProcessorEnumerator(&content, &enumerator);
    if (FAILED(hr))
    {
        PrintResult(L"CreateVideoProcessorEnumerator", hr);
        return 6;
    }

    UINT formatFlags = 0;
    hr = enumerator->CheckVideoProcessorFormat(DXGI_FORMAT_NV12, &formatFlags);
    if (FAILED(hr) ||
        (formatFlags & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_INPUT) == 0 ||
        (formatFlags & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT) == 0)
    {
        std::wcerr << L"The driver does not expose NV12 video-processor input/output support.\n";
        return 7;
    }

    ComPtr<ID3D11VideoProcessor> processor;
    hr = videoDevice->CreateVideoProcessor(enumerator.Get(), 0, &processor);
    if (FAILED(hr))
    {
        PrintResult(L"CreateVideoProcessor", hr);
        return 8;
    }

    UINT reportedAvailable = 0;
    const HRESULT getHr = videoContext->VideoProcessorGetStreamExtension(
        processor.Get(), 0, &NvidiaPpeInterfaceGuid,
        sizeof(reportedAvailable), &reportedAvailable);
    PrintResult(L"Get NVIDIA VSR extension", getHr);
    std::wcout << L"Driver-reported availability: " << reportedAvailable << L'\n';

    NvidiaVsrExtension extension;
    hr = videoContext->VideoProcessorSetStreamExtension(
        processor.Get(), 0, &NvidiaPpeInterfaceGuid,
        sizeof(extension), &extension);
    PrintResult(L"Enable NVIDIA VSR extension", hr);
    if (FAILED(hr))
        return 9;

    extension.enable = 0;
    const HRESULT disableHr = videoContext->VideoProcessorSetStreamExtension(
        processor.Get(), 0, &NvidiaPpeInterfaceGuid,
        sizeof(extension), &extension);
    PrintResult(L"Disable NVIDIA VSR extension", disableHr);

    std::wcout << L"\nThe unsigned probe reached NVIDIA RTX VSR through the Windows D3D11 driver.\n";
    return SUCCEEDED(disableHr) ? 0 : 10;
}
