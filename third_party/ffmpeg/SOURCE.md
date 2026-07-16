# FFmpeg corresponding source

LocalVSR portable packages use this unmodified shared LGPLv3 build:

- Archive: `ffmpeg-n6.1.2-2-gb534cc666e-win64-lgpl-shared-6.1.zip`
- BtbN release tag: `autobuild-2024-08-31-12-50`
- Archive SHA-256: `f1c49b1016c24c82bc1677b23070bc0fd54db642c94d2ebea73d4219ee5b4dfd`
- FFmpeg commit: `b534cc666e0a770a4bb474d71569378635e9d464`

Binary archive:

https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2024-08-31-12-50/ffmpeg-n6.1.2-2-gb534cc666e-win64-lgpl-shared-6.1.zip

Upstream source:

https://github.com/FFmpeg/FFmpeg/tree/b534cc666e0a770a4bb474d71569378635e9d464

Build scripts:

https://github.com/BtbN/FFmpeg-Builds/tree/autobuild-2024-08-31-12-50

`tools/build-release.ps1` downloads archives of the exact FFmpeg source and BtbN build scripts into `dist/third-party-source/`. Publish those archives beside every binary release. The FFmpeg DLLs remain separate and replaceable in the portable package.
