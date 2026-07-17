# Changelog

## 0.2.0-beta

- Added a separate headless `LocalVSR.Cli.exe` with media probing, all export controls, JSON results, progress on stderr, deterministic exit codes, and Ctrl+C cleanup.
- Fixed PNG and other still-image inputs with odd pixel dimensions by normalizing them for the NV12 VSR pipeline.
- Added optional 2× and 4× AI frame multiplication with the effective output FPS shown beside the multiplier.
- Added GPU inference through the portable RIFE v4.6 ncnn Vulkan runtime on RTX 20/30/40/50 GPUs.
- Added a persistent bounded-memory RIFE worker that loads the model once and streams raw frames without temporary images, with two ordered inference jobs, exact output frame counts, scene-cut bypass, audio timing preservation, cancellation cleanup, and multiplier-aware output names.
- Added custom start/end timestamps for exporting only a selected video interval, including matched audio and `-clip` output naming.
- Added a CUDA/NVDEC capability probe and hardware decode for supported inputs, with automatic software-decode fallback.
- Added double-buffered native VSR submission/readback and a continuous decode/interpolation/VSR/NVENC pipeline.
- Added opening a media file passed on the command line, including Windows **Open with** workflows.

## 0.1.0-beta

- Added local drag-and-drop video and image upscaling through the NVIDIA D3D11 VSR path.
- Added output scaling choices through 4K.
- Added fixed VSR quality levels 1–4 and Auto with temporary driver-setting restoration.
- Added NVENC H.264 output, copied audio/subtitles/metadata where supported, progress, and cancellation.
- Added a compact responsive Windows UI and self-contained .NET runtime.
- Replaced the GPL FFmpeg bundle with replaceable LGPLv3 shared binaries for public distribution.
