# Third-party notices

LocalVSR is an independent project. It is not affiliated with, sponsored by, or endorsed by NVIDIA Corporation.

## NVIDIA names and system software

The application supports NVIDIA RTX GPUs and refers to NVIDIA technologies descriptively. It does not distribute NVIDIA SDK files, drivers, logos, or `nvapi64.dll`; it loads the copy installed by the user's NVIDIA graphics driver.

NVIDIA, GeForce, RTX, NVENC, and related marks are trademarks or registered trademarks of NVIDIA Corporation in the United States and other countries. All rights remain with their respective owners.

The optional VSR quality override uses undocumented video-settings entry points found in the installed NVIDIA driver. These entry points are not part of NVIDIA's supported public NVAPI SDK and may change or disappear. The application detects support at runtime and falls back safely when they are unavailable.

## FFmpeg

Portable builds include separate, replaceable FFmpeg executables and shared libraries from the BtbN FFmpeg Builds project:

- Build: `ffmpeg-n6.1.2-2-gb534cc666e-win64-lgpl-shared-6.1`
- Build date/tag: `autobuild-2024-08-31-12-50`
- FFmpeg source commit: `b534cc666e0a770a4bb474d71569378635e9d464`
- Binary archive SHA-256: `f1c49b1016c24c82bc1677b23070bc0fd54db642c94d2ebea73d4219ee5b4dfd`
- Binary archive: https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2024-08-31-12-50
- FFmpeg source: https://github.com/FFmpeg/FFmpeg/tree/b534cc666e0a770a4bb474d71569378635e9d464
- Build scripts: https://github.com/BtbN/FFmpeg-Builds/tree/autobuild-2024-08-31-12-50

This build disables GPL-only dependencies including x264 and x265 and is distributed under the GNU Lesser General Public License version 3. The full license text is included in `licenses/FFmpeg-LGPLv3.txt` in release packages. Corresponding source archives and reproducible build scripts are prepared by `tools/build-release.ps1` alongside the portable ZIP.

LocalVSR invokes `ffmpeg.exe` and `ffprobe.exe` as separate programs. FFmpeg is not renamed, modified, statically linked into LocalVSR, or represented as part of the LocalVSR source code.

## RIFE ncnn Vulkan frame interpolation

Portable builds include a LocalVSR streaming worker built from the source of `rife-ncnn-vulkan` release `20221029`, plus selected unmodified release files retained as a compatibility fallback:

- Project and source: https://github.com/nihui/rife-ncnn-vulkan
- Source commit: `a7532fc3f9f8f008cd6eecd6f2ffe2a9698e0cf7`
- Windows archive SHA-256: `d8e4d772d26cd8006ef0ad0bc82eb191b53c68677d1ae2f42506d74cbbbea606`
- Included model: RIFE v4.6
- License: MIT; the license text is included as `licenses/RIFE-ncnn-Vulkan-MIT.txt`

The neural model originates from the RIFE/Practical-RIFE project and its model releases are made available under that project's MIT license: https://github.com/hzwer/Practical-RIFE

`RifeProcessor.exe` statically links pinned ncnn (`b4ba207c18d3103d6df890c0e3a97b469b196b26`, BSD 3-Clause and bundled third-party terms), libwebp (`5abb55823bb6196a918dd87202b2f32bbaff4c18`, BSD-style license), and the pinned upstream RIFE implementation. Their license texts are included in release packages. LocalVSR's own worker frontend is distributed under the project MIT license and its complete source and reproducible build script are under `native/RifeProcessor/`.

Portable builds also retain the upstream `rife-ncnn-vulkan.exe` unchanged. LocalVSR invokes it only as a separate compatibility fallback when the persistent worker is unavailable.

The upstream Windows archive also includes `vcomp140.dll`, a Microsoft Visual C++ OpenMP runtime redistributable. Microsoft Visual C++ licensing terms apply: https://visualstudio.microsoft.com/license-terms/

## Microsoft .NET

Portable builds contain the Microsoft .NET 8 runtime and Windows Desktop runtime. .NET is licensed under the MIT License and includes third-party components under their respective licenses. The runtime license and third-party notices are included under `licenses/` in release packages.

- .NET runtime source and license: https://github.com/dotnet/runtime
- Windows Forms source and license: https://github.com/dotnet/winforms

## Research references

The Windows D3D11 VSR integration was independently implemented using Windows SDK interfaces after reviewing public implementations in Chromium and VLC. No Chromium or VLC binary is distributed.

- Chromium source reference: https://chromium.googlesource.com/chromium/src/+/8d25b98888f4116545f624b268d62f77bde903a8/ui/gl/swap_chain_presenter.cc
- VLC source reference: https://mailman.videolan.org/pipermail/vlc-commits/2023-April/068304.html

Chromium is licensed primarily under a BSD-style license. VLC is licensed under the GNU General Public License version 2 or later, with some libraries under the GNU Lesser General Public License.

This notice is informational and is not legal advice.
