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
- Optional 2× or 4× AI frame multiplication with the calculated effective FPS shown in the UI
- Optional start and end timestamps so only a selected part of a video is processed
- NVIDIA NVENC H.264 output
- Audio, subtitles, chapters, and metadata copied where the output container supports them
- Progress, cancellation, output naming, and restoration of the user's previous global VSR setting
- No cloud processing and no modification of the original file

Current pipeline:

```text
FFmpeg NVDEC (when supported) → [optional persistent Vulkan AI frame interpolation] → D3D11 NVIDIA VSR → FFmpeg NVENC encode
```

If the input cannot be decoded through NVIDIA CUDA/NVDEC, LocalVSR automatically falls back to FFmpeg's software decoder.

## Download and run

The intended distribution is a portable ZIP, not an installer:

1. Download `LocalVSR-<version>-win-x64.zip` from the project's Releases page.
2. Extract the complete folder. Do not run the executable from inside the ZIP.
3. Run `LocalVSR.exe`.

No separate .NET installation is required. Keep all files and the `rife-v4.6` model folder from the ZIP beside `LocalVSR.exe`.

Unsigned beta builds may trigger Microsoft Defender SmartScreen. Verify that the download came from the project's official release and compare its SHA-256 checksum before running it.

## Requirements

- 64-bit Windows 10 or Windows 11
- NVIDIA RTX GPU
- NVIDIA graphics driver with RTX Video Super Resolution support
- Vulkan support from the installed NVIDIA graphics driver for frame multiplication
- Sufficient free disk space for the upscaled output

## Frame multiplication

Enable **Frame multiplication** after selecting a video, then choose:

- `2×`: 24 → 48 FPS, 30 → 60 FPS, or 60 → 120 FPS
- `4×`: 24 → 96 FPS or 30 → 120 FPS

The UI shows the exact effective frame rate, including fractional rates such as 23.976 → 47.952 FPS. Choices that would exceed 240 FPS are not offered. Still images do not expose this control.

Frame multiplication uses the local RIFE v4.6 neural interpolation model through ncnn and Vulkan. It runs on the RTX GPU, but it is not DLSS Frame Generation and does not impersonate or unlock an RTX 40/50 feature. Interpolation runs at the source resolution before VSR, so VSR enhances the original and generated frames consistently.

The RIFE model is loaded once per export and frames stream through a bounded in-memory pipeline; no temporary image sequence is written. Two ordered inference jobs overlap interpolation work, while the native VSR worker overlaps GPU submission with readback. Hard scene cuts are detected and bypassed so the model does not blend unrelated scenes. The output has an exact multiplier-adjusted frame count, while source duration and audio timing remain unchanged.

## Processing part of a video

After selecting a video, enter a **Start** and **End** timestamp in the Range row. Seconds, `MM:SS`, and `HH:MM:SS` are accepted, including decimal fractions. **Full video** resets both fields. Video, audio, subtitles, chapters, and progress are limited to the selected interval, and partial exports receive `-clip` in the output name. Still images ignore this control.

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
- Git
- PowerShell 7 or Windows PowerShell 5.1

A separate Vulkan SDK installation is not required. The worker build downloads verified, pinned Vulkan headers and creates its loader import library from the Windows Vulkan loader already installed with the graphics driver.

Create a complete portable release locally:

```powershell
.\tools\build-release.ps1
```

The script:

- Downloads and verifies the pinned LGPLv3 FFmpeg build.
- Downloads verified, pinned RIFE/ncnn/libwebp sources and builds the persistent interpolation worker.
- Builds the native D3D11 VSR worker.
- Publishes the self-contained .NET application.
- Replaces `bin/Release/` with the single latest runnable build.
- Creates the portable ZIP and SHA-256 checksum under `dist/`.
- Removes superseded versioned LocalVSR builds from `dist/`.
- Downloads the exact FFmpeg and build-script source archives under `dist/third-party-source/` for release alongside the binary.

For local testing, always launch `bin/Release/LocalVSR.exe`. Packaging scratch files and ordinary compiler output stay under the ignored `obj/` directory.

For development builds:

```powershell
.\tools\install-ffmpeg.ps1
.\tools\install-rife.ps1
.\native\RifeProcessor\build.ps1
.\native\VsrProcessor\build.ps1
dotnet build .\RtxLocalVideo.csproj -c Release
```

## Implementation notes

LocalVSR does not impersonate a browser and does not redistribute NVIDIA's RTX Video SDK, Optical Flow SDK, NVAPI DLL, or drivers. The native worker uses Windows D3D11 video-processing interfaces and a driver extension also exercised by public Chromium and VLC implementations. Frame multiplication is provided by a LocalVSR streaming worker built from pinned, separately licensed open-source RIFE/ncnn sources; the unmodified upstream command-line program remains packaged as a compatibility fallback. See the third-party notices for exact versions and licenses.

`native/VsrProbe` is a minimal standalone diagnostic that queries and toggles the stream extension using Windows SDK interfaces. It is a development tool and is not included in release packages.

Research references:

- Chromium: https://chromium.googlesource.com/chromium/src/+/8d25b98888f4116545f624b268d62f77bde903a8/ui/gl/swap_chain_presenter.cc
- VLC: https://mailman.videolan.org/pipermail/vlc-commits/2023-April/068304.html

## License and notices

LocalVSR source code is licensed under the [MIT License](LICENSE). Third-party software remains under its respective license; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Portable packages use separate, replaceable FFmpeg shared libraries under LGPLv3. Their license, exact build identity, source commit, build scripts, and source-packaging procedure are documented in the third-party notices.
