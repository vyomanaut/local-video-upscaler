# Release checklist

## Before building

- Confirm `main` builds without warnings.
- Build and test `native/VsrProcessor` on an NVIDIA RTX machine.
- Run one video export and one still-image export.
- Test VSR Auto and level 4, including restoration after success and cancellation.
- Confirm FFmpeg reports the pinned LGPL build and `h264_nvenc` is available.
- Confirm 2× and 4× frame multiplication, effective FPS, exact frame counts, audio timing, scene-cut bypass, and cancellation cleanup.
- Confirm the persistent RIFE worker/model checksums and Vulkan inference on the target RTX GPU, then separately exercise the upstream-CLI fallback.
- Confirm CUDA/NVDEC is selected for a supported H.264/HEVC input and software decode is selected for an unsupported input.
- Review `THIRD-PARTY-NOTICES.md` and the generated license directory.

## Build

Run:

```powershell
.\tools\build-release.ps1
```

This creates:

- `bin/Release/LocalVSR.exe` and its required runtime files as the single latest local build
- `dist/LocalVSR-0.2.0-beta-win-x64.zip`
- `dist/LocalVSR-0.2.0-beta-win-x64.zip.sha256`
- `dist/third-party-source/`

Older versioned LocalVSR artifacts are removed automatically; release-build scratch files remain under `obj/`.

## Before publishing

- Extract the ZIP into a clean directory and launch `LocalVSR.exe`.
- Confirm FFmpeg DLLs remain beside the application and are not folded into the EXE.
- Confirm `RifeProcessor.exe`, `rife-ncnn-vulkan.exe`, `vcomp140.dll`, and `rife-v4.6/` remain beside the application.
- Scan the ZIP and executable with Microsoft Defender.
- If available, Authenticode-sign `LocalVSR.exe`, `VsrProcessor.exe`, and the final ZIP workflow artifacts before publishing.
- Test the downloaded artifact on a second Windows PC or Windows Sandbox.
- Clearly label the release as beta and document the SmartScreen warning for unsigned builds.
- Upload the portable ZIP, checksum, and the contents of `dist/third-party-source/` as release assets.
- Do not commit `bin/`, `obj/`, `dist/`, FFmpeg binaries, or native build output to Git.

## Compatibility sampling

Before calling the project stable, test at least:

- RTX 20, 30, 40, and 50 series where available.
- Desktop and Optimus laptop configurations.
- A current driver and at least one older VSR-capable driver.
- 720p→1440p, 1080p→4K, image export, cancellation, and paths containing spaces/non-ASCII characters.
