# Release checklist

## Before building

- Confirm `main` builds without warnings.
- Build and test `native/VsrProcessor` on an NVIDIA RTX machine.
- Run one video export and one still-image export.
- Test VSR Auto and level 4, including restoration after success and cancellation.
- Confirm FFmpeg reports the pinned LGPL build and `h264_nvenc` is available.
- Review `THIRD-PARTY-NOTICES.md` and the generated license directory.

## Build

Run:

```powershell
.\tools\build-release.ps1
```

This creates:

- `dist/LocalVSR-0.1.0-beta-win-x64.zip`
- `dist/LocalVSR-0.1.0-beta-win-x64.zip.sha256`
- `dist/third-party-source/`

## Before publishing

- Extract the ZIP into a clean directory and launch `LocalVSR.exe`.
- Confirm FFmpeg DLLs remain beside the application and are not folded into the EXE.
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
