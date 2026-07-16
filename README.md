# LocalVSR

Local video upscaling for NVIDIA RTX GPUs.

LocalVSR is an independent, open-source Windows application. It is not affiliated with, sponsored by, or endorsed by NVIDIA Corporation. NVIDIA, GeForce, RTX, and NVENC are trademarks or registered trademarks of NVIDIA Corporation.

> Beta software: keep the original media file. LocalVSR always writes a separate output, but its undocumented driver integration may behave differently across NVIDIA driver releases.

## What it does

Drop a video or still image into the application, choose an output scale and VSR quality, and save an upscaled local copy. Processing stays on the local PC.

- Video input through FFmpeg: MP4, MKV, WebM, AVI, MOV, M4V, WMV, TS, M2TS, OGV, and many other formats
- Image input: PNG, JPEG, BMP, and TIFF
- 1.5×, 2×, 3×, and 4× choices when the result remains within the driver's 4K processing limit
- NVIDIA VSR quality slider with Auto and fixed levels 1–4
- NVIDIA NVENC H.264 output
- Audio, subtitles, chapters, and metadata copied where the output container supports them
- Progress, cancellation, output naming, and restoration of the user's previous global VSR setting
- No cloud processing and no modification of the original file

Current pipeline:

```text
FFmpeg decode → raw NV12 → D3D11 NVIDIA VSR → raw NV12 → FFmpeg NVENC encode
```

## Download and run

The intended distribution is a portable ZIP, not an installer:

1. Download `LocalVSR-<version>-win-x64.zip` from the project's Releases page.
2. Extract the complete folder. Do not run the executable from inside the ZIP.
3. Run `LocalVSR.exe`.

No separate .NET installation is required. Keep the FFmpeg DLLs, `ffmpeg.exe`, `ffprobe.exe`, and `VsrProcessor.exe` beside `LocalVSR.exe`.

Unsigned beta builds may trigger Microsoft Defender SmartScreen. Verify that the download came from the project's official release and compare its SHA-256 checksum before running it.

## Requirements

- 64-bit Windows 10 or Windows 11
- NVIDIA RTX GPU
- NVIDIA graphics driver with RTX Video Super Resolution support
- Sufficient free disk space for the upscaled output

## VSR quality control

The D3D11 VSR stream extension turns Super Resolution on for a processing stream but does not carry the Auto/1–4 quality choice. That choice is global NVIDIA driver state.

When a fixed level or Auto is selected, LocalVSR:

1. Reads the current NVIDIA VSR value.
2. Applies and verifies the chosen value before export.
3. Processes the media.
4. Restores the previous value afterward, including cancellation and handled failures.

The video-settings entry points used for this override exist in NVIDIA's driver and NVIDIA App, but are not part of the supported public NVAPI SDK. LocalVSR detects them at runtime. If they are unavailable or have changed, the control is disabled and the NVIDIA system setting is left untouched.

Level 4 selects the highest fixed VSR quality. It does not command a particular total-GPU-utilization percentage; decode, frame transfer, VSR inference, and NVENC use different GPU engines and can bottleneck independently.

## Build from source

Prerequisites:

- Visual Studio 2022 Build Tools with the Desktop development with C++ workload
- .NET 8 SDK
- PowerShell 7 or Windows PowerShell 5.1

Create a complete portable release locally:

```powershell
.\tools\build-release.ps1
```

The script:

- Downloads and verifies the pinned LGPLv3 FFmpeg build.
- Builds the native D3D11 VSR worker.
- Publishes the self-contained .NET application.
- Creates the portable ZIP and SHA-256 checksum under `dist/`.
- Downloads the exact FFmpeg and build-script source archives under `dist/third-party-source/` for release alongside the binary.

For development builds:

```powershell
.\tools\install-ffmpeg.ps1
.\native\VsrProcessor\build.ps1
dotnet build .\RtxLocalVideo.csproj -c Release
```

## Implementation notes

LocalVSR does not impersonate a browser and does not redistribute NVIDIA's RTX Video SDK, NVAPI DLL, or drivers. The native worker uses Windows D3D11 video-processing interfaces and a driver extension also exercised by public Chromium and VLC implementations.

`native/VsrProbe` is a minimal standalone diagnostic that queries and toggles the stream extension using Windows SDK interfaces. It is a development tool and is not included in release packages.

Research references:

- Chromium: https://chromium.googlesource.com/chromium/src/+/8d25b98888f4116545f624b268d62f77bde903a8/ui/gl/swap_chain_presenter.cc
- VLC: https://mailman.videolan.org/pipermail/vlc-commits/2023-April/068304.html

## License and notices

LocalVSR source code is licensed under the [MIT License](LICENSE). Third-party software remains under its respective license; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Portable packages use separate, replaceable FFmpeg shared libraries under LGPLv3. Their license, exact build identity, source commit, build scripts, and source-packaging procedure are documented in the third-party notices.
