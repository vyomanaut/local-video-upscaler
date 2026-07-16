# RIFE ncnn Vulkan source, binary, and model provenance

LocalVSR portable packages include a persistent streaming worker built from
the pinned `rife-ncnn-vulkan` source, along with selected unmodified files from
the official Windows release as a compatibility fallback:

- Project: https://github.com/nihui/rife-ncnn-vulkan
- Release/tag: `20221029`
- Source commit: `a7532fc3f9f8f008cd6eecd6f2ffe2a9698e0cf7`
- Binary archive: `rife-ncnn-vulkan-20221029-windows.zip`
- Archive SHA-256: `d8e4d772d26cd8006ef0ad0bc82eb191b53c68677d1ae2f42506d74cbbbea606`
- ncnn submodule commit: `b4ba207c18d3103d6df890c0e3a97b469b196b26`
- libwebp submodule commit: `5abb55823bb6196a918dd87202b2f32bbaff4c18`
- glslang submodule commit: `86ff4bca1ddc7e2262f119c16e7228d0efb67610`
- Vulkan Headers tag: `v1.3.239`

`tools/install-rife.ps1` downloads and verifies the pinned upstream archive,
then extracts only the Windows executable, its OpenMP runtime, the RIFE v4.6
model, and the upstream license/readme.

`native/RifeProcessor/build.ps1` sparsely checks out the pinned RIFE source,
downloads and verifies the exact ncnn, libwebp, glslang, and Vulkan Headers
archives, and builds `RifeProcessor.exe`. The LocalVSR frontend source is in
`native/RifeProcessor/RifeProcessor.cpp`; the binary statically links those
pinned open-source components. No separately installed Vulkan SDK is needed.

At runtime LocalVSR prefers this worker so the model stays loaded while raw
frames stream through memory. If the worker is absent, LocalVSR can invoke the
unmodified upstream `rife-ncnn-vulkan.exe` as a separate-process fallback.
