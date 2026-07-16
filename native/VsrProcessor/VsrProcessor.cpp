// Offline NVIDIA RTX VSR frame processor.
//
// Input:  raw NV12 frames on stdin at --input-width/--input-height
// Output: raw NV12 frames on stdout at --output-width/--output-height
// Logs:   stderr
//
// Uses only Windows D3D11 interfaces and the same driver extension used by
// open-source Chromium and VLC. No NVIDIA SDK is linked or redistributed.

#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <fcntl.h>
#include <io.h>

#include <algorithm>
#include <array>
#include <cstdint>
#include <iostream>
#include <limits>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace
{
constexpr UINT NvidiaVendorId = 0x10DE;
#ifndef LOCALVSR_PIPELINE_DEPTH
#define LOCALVSR_PIPELINE_DEPTH 2
#endif
constexpr size_t PipelineDepth = LOCALVSR_PIPELINE_DEPTH;
static_assert(PipelineDepth == 1 || PipelineDepth == 2);
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

struct Options
{
    UINT inputWidth = 0;
    UINT inputHeight = 0;
    UINT outputWidth = 0;
    UINT outputHeight = 0;
    UINT frameRateNumerator = 30;
    UINT frameRateDenominator = 1;
};

bool ParseUnsigned(const wchar_t* value, UINT& result)
{
    wchar_t* end = nullptr;
    const unsigned long parsed = wcstoul(value, &end, 10);
    if (end == value || *end != L'\0' || parsed == 0 || parsed > std::numeric_limits<UINT>::max())
        return false;
    result = static_cast<UINT>(parsed);
    return true;
}

bool ParseOptions(int argc, wchar_t** argv, Options& options)
{
    for (int i = 1; i < argc; i += 2)
    {
        if (i + 1 >= argc)
            return false;

        UINT value = 0;
        if (!ParseUnsigned(argv[i + 1], value))
            return false;

        const std::wstring name = argv[i];
        if (name == L"--input-width") options.inputWidth = value;
        else if (name == L"--input-height") options.inputHeight = value;
        else if (name == L"--output-width") options.outputWidth = value;
        else if (name == L"--output-height") options.outputHeight = value;
        else if (name == L"--fps-numerator") options.frameRateNumerator = value;
        else if (name == L"--fps-denominator") options.frameRateDenominator = value;
        else return false;
    }

    return options.inputWidth >= 90 && options.inputHeight >= 90 &&
           options.outputWidth > options.inputWidth && options.outputHeight > options.inputHeight &&
           (options.inputWidth % 2) == 0 && (options.inputHeight % 2) == 0 &&
           (options.outputWidth % 2) == 0 && (options.outputHeight % 2) == 0;
}

void PrintHr(const wchar_t* operation, HRESULT result)
{
    std::wcerr << operation << L" failed (HRESULT 0x" << std::hex
               << static_cast<unsigned long>(result) << std::dec << L").\n";
}

class VsrProcessor
{
public:
    bool Initialize(const Options& options)
    {
        options_ = options;

        ComPtr<IDXGIFactory1> factory;
        HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
        if (FAILED(hr)) return Fail(L"CreateDXGIFactory1", hr);

        ComPtr<IDXGIAdapter1> adapter;
        for (UINT index = 0;; ++index)
        {
            ComPtr<IDXGIAdapter1> candidate;
            if (factory->EnumAdapters1(index, &candidate) == DXGI_ERROR_NOT_FOUND)
                break;

            DXGI_ADAPTER_DESC1 description{};
            if (SUCCEEDED(candidate->GetDesc1(&description)) && description.VendorId == NvidiaVendorId)
            {
                adapter = candidate;
                std::wcerr << L"RTX VSR adapter: " << description.Description << L'\n';
                break;
            }
        }
        if (!adapter)
        {
            std::wcerr << L"No NVIDIA adapter was found.\n";
            return false;
        }

        constexpr D3D_FEATURE_LEVEL levels[] = {
            D3D_FEATURE_LEVEL_12_1, D3D_FEATURE_LEVEL_12_0,
            D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0
        };
        D3D_FEATURE_LEVEL selectedLevel{};
        hr = D3D11CreateDevice(
            adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
            levels, ARRAYSIZE(levels), D3D11_SDK_VERSION,
            &device_, &selectedLevel, &context_);
        if (FAILED(hr)) return Fail(L"D3D11CreateDevice", hr);

        hr = device_.As(&videoDevice_);
        if (FAILED(hr)) return Fail(L"Query ID3D11VideoDevice", hr);
        hr = context_.As(&videoContext_);
        if (FAILED(hr)) return Fail(L"Query ID3D11VideoContext", hr);

        D3D11_VIDEO_PROCESSOR_CONTENT_DESC content{};
        content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        content.InputFrameRate = {options.frameRateNumerator, options.frameRateDenominator};
        content.InputWidth = options.inputWidth;
        content.InputHeight = options.inputHeight;
        content.OutputFrameRate = content.InputFrameRate;
        content.OutputWidth = options.outputWidth;
        content.OutputHeight = options.outputHeight;
        content.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;

        hr = videoDevice_->CreateVideoProcessorEnumerator(&content, &enumerator_);
        if (FAILED(hr)) return Fail(L"CreateVideoProcessorEnumerator", hr);

        UINT formatFlags = 0;
        hr = enumerator_->CheckVideoProcessorFormat(DXGI_FORMAT_NV12, &formatFlags);
        if (FAILED(hr) ||
            (formatFlags & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_INPUT) == 0 ||
            (formatFlags & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT) == 0)
        {
            std::wcerr << L"NV12 video-processor input/output is not supported.\n";
            return false;
        }

        hr = videoDevice_->CreateVideoProcessor(enumerator_.Get(), 0, &processor_);
        if (FAILED(hr)) return Fail(L"CreateVideoProcessor", hr);

        if (!CreateTextures()) return false;
        if (!CreateViews()) return false;

        RECT sourceRect{0, 0, static_cast<LONG>(options.inputWidth), static_cast<LONG>(options.inputHeight)};
        RECT destinationRect{0, 0, static_cast<LONG>(options.outputWidth), static_cast<LONG>(options.outputHeight)};
        videoContext_->VideoProcessorSetStreamFrameFormat(
            processor_.Get(), 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
        videoContext_->VideoProcessorSetStreamSourceRect(processor_.Get(), 0, TRUE, &sourceRect);
        videoContext_->VideoProcessorSetStreamDestRect(processor_.Get(), 0, TRUE, &destinationRect);
        videoContext_->VideoProcessorSetOutputTargetRect(processor_.Get(), TRUE, &destinationRect);

        UINT available = 0;
        hr = videoContext_->VideoProcessorGetStreamExtension(
            processor_.Get(), 0, &NvidiaPpeInterfaceGuid, sizeof(available), &available);
        if (FAILED(hr) || available == 0)
        {
            std::wcerr << L"The NVIDIA driver reports that RTX VSR is unavailable.\n";
            return false;
        }

        NvidiaVsrExtension extension;
        hr = videoContext_->VideoProcessorSetStreamExtension(
            processor_.Get(), 0, &NvidiaPpeInterfaceGuid, sizeof(extension), &extension);
        if (FAILED(hr)) return Fail(L"Enable NVIDIA VSR extension", hr);

        std::wcerr << L"RTX VSR enabled: " << options.inputWidth << L'x' << options.inputHeight
                   << L" -> " << options.outputWidth << L'x' << options.outputHeight << L'\n';
        return true;
    }

    bool SubmitFrame(const std::vector<std::uint8_t>& input, size_t slot)
    {
        if (slot >= PipelineDepth) return false;
        context_->UpdateSubresource(
            inputTexture_.Get(), 0, nullptr, input.data(),
            options_.inputWidth,
            options_.inputWidth * options_.inputHeight * 3 / 2);

        D3D11_VIDEO_PROCESSOR_STREAM stream{};
        stream.Enable = TRUE;
        stream.pInputSurface = inputView_.Get();

        HRESULT hr = videoContext_->VideoProcessorBlt(
            processor_.Get(), outputViews_[slot].Get(), 0, 1, &stream);
        if (FAILED(hr)) return Fail(L"VideoProcessorBlt", hr);

        context_->CopyResource(stagingTextures_[slot].Get(), outputTextures_[slot].Get());
        return true;
    }

    bool WriteFrame(size_t slot)
    {
        if (slot >= PipelineDepth) return false;
        D3D11_MAPPED_SUBRESOURCE mapped{};
        const HRESULT hr = context_->Map(
            stagingTextures_[slot].Get(), 0, D3D11_MAP_READ, 0, &mapped);
        if (FAILED(hr)) return Fail(L"Map output texture", hr);

        const auto* bytes = static_cast<const std::uint8_t*>(mapped.pData);
        const UINT totalRows = options_.outputHeight + options_.outputHeight / 2;
        bool writeOk = true;
        for (UINT row = 0; row < totalRows; ++row)
        {
            std::cout.write(
                reinterpret_cast<const char*>(bytes + static_cast<size_t>(row) * mapped.RowPitch),
                options_.outputWidth);
            if (!std::cout)
            {
                writeOk = false;
                break;
            }
        }
        context_->Unmap(stagingTextures_[slot].Get(), 0);
        return writeOk;
    }

private:
    bool CreateTextures()
    {
        D3D11_TEXTURE2D_DESC input{};
        input.Width = options_.inputWidth;
        input.Height = options_.inputHeight;
        input.MipLevels = 1;
        input.ArraySize = 1;
        input.Format = DXGI_FORMAT_NV12;
        input.SampleDesc.Count = 1;
        input.Usage = D3D11_USAGE_DEFAULT;
        input.BindFlags = D3D11_BIND_DECODER;

        HRESULT hr = device_->CreateTexture2D(&input, nullptr, &inputTexture_);
        if (FAILED(hr)) return Fail(L"Create input texture", hr);

        D3D11_TEXTURE2D_DESC output = input;
        output.Width = options_.outputWidth;
        output.Height = options_.outputHeight;
        output.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_VIDEO_ENCODER;
        for (size_t slot = 0; slot < PipelineDepth; ++slot)
        {
            hr = device_->CreateTexture2D(&output, nullptr, &outputTextures_[slot]);
            if (FAILED(hr)) return Fail(L"Create output texture", hr);

            D3D11_TEXTURE2D_DESC staging = output;
            staging.Usage = D3D11_USAGE_STAGING;
            staging.BindFlags = 0;
            staging.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            hr = device_->CreateTexture2D(&staging, nullptr, &stagingTextures_[slot]);
            if (FAILED(hr)) return Fail(L"Create staging texture", hr);
        }
        return true;
    }

    bool CreateViews()
    {
        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inputDesc{};
        inputDesc.FourCC = 0;
        inputDesc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
        inputDesc.Texture2D.MipSlice = 0;
        inputDesc.Texture2D.ArraySlice = 0;
        HRESULT hr = videoDevice_->CreateVideoProcessorInputView(
            inputTexture_.Get(), enumerator_.Get(), &inputDesc, &inputView_);
        if (FAILED(hr)) return Fail(L"Create video-processor input view", hr);

        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outputDesc{};
        outputDesc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        outputDesc.Texture2D.MipSlice = 0;
        for (size_t slot = 0; slot < PipelineDepth; ++slot)
        {
            hr = videoDevice_->CreateVideoProcessorOutputView(
                outputTextures_[slot].Get(), enumerator_.Get(), &outputDesc, &outputViews_[slot]);
            if (FAILED(hr)) return Fail(L"Create video-processor output view", hr);
        }
        return true;
    }

    static bool Fail(const wchar_t* operation, HRESULT hr)
    {
        PrintHr(operation, hr);
        return false;
    }

    Options options_{};
    ComPtr<ID3D11Device> device_;
    ComPtr<ID3D11DeviceContext> context_;
    ComPtr<ID3D11VideoDevice> videoDevice_;
    ComPtr<ID3D11VideoContext> videoContext_;
    ComPtr<ID3D11VideoProcessorEnumerator> enumerator_;
    ComPtr<ID3D11VideoProcessor> processor_;
    ComPtr<ID3D11Texture2D> inputTexture_;
    std::array<ComPtr<ID3D11Texture2D>, PipelineDepth> outputTextures_;
    std::array<ComPtr<ID3D11Texture2D>, PipelineDepth> stagingTextures_;
    ComPtr<ID3D11VideoProcessorInputView> inputView_;
    std::array<ComPtr<ID3D11VideoProcessorOutputView>, PipelineDepth> outputViews_;
};
}

int wmain(int argc, wchar_t** argv)
{
    Options options;
    if (!ParseOptions(argc, argv, options))
    {
        std::wcerr << L"Usage: VsrProcessor.exe --input-width W --input-height H "
                      L"--output-width W --output-height H "
                      L"[--fps-numerator N --fps-denominator D]\n";
        return 2;
    }

    _setmode(_fileno(stdin), _O_BINARY);
    _setmode(_fileno(stdout), _O_BINARY);
    std::ios::sync_with_stdio(false);

    VsrProcessor processor;
    if (!processor.Initialize(options))
        return 3;

    const size_t inputFrameSize =
        static_cast<size_t>(options.inputWidth) * options.inputHeight * 3 / 2;
    std::vector<std::uint8_t> frame(inputFrameSize);
    std::uint64_t frameNumber = 0;
    bool hasPendingFrame = false;
    size_t pendingSlot = 0;
    size_t nextSlot = 0;

    while (true)
    {
        std::cin.read(reinterpret_cast<char*>(frame.data()), static_cast<std::streamsize>(frame.size()));
        const auto bytesRead = static_cast<size_t>(std::cin.gcount());
        if (bytesRead == 0)
            break;
        if (bytesRead != frame.size())
        {
            std::wcerr << L"Decoder ended with a partial NV12 frame.\n";
            return 4;
        }

        if (!processor.SubmitFrame(frame, nextSlot))
            return std::cout ? 5 : 0; // A closed downstream pipe is a normal cancellation path.

        if (hasPendingFrame && !processor.WriteFrame(pendingSlot))
            return std::cout ? 5 : 0;

        pendingSlot = nextSlot;
        nextSlot = (nextSlot + 1) % PipelineDepth;
        hasPendingFrame = true;

        ++frameNumber;
        if (frameNumber % 120 == 0)
            std::wcerr << L"Processed " << frameNumber << L" frames\n";
    }

    if (hasPendingFrame && !processor.WriteFrame(pendingSlot))
        return std::cout ? 5 : 0;

    std::wcerr << L"Completed " << frameNumber << L" frames\n";
    return 0;
}
