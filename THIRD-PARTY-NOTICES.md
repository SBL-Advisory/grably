# Third-party notices

Grably is MIT licensed (see [LICENSE](LICENSE)). It ships and uses the following third-party software.

## FFmpeg

Grably records and exports video with [FFmpeg](https://ffmpeg.org), which it runs as a separate program (`ffmpeg.exe` and `ffprobe.exe` with their DLLs, in the `ffmpeg` folder next to `Grably.exe`). Grably does not link to FFmpeg.

- **Build:** `ffmpeg-n9.0.2-39-g7adfbe40dc-win64-lgpl-shared-9.0`, from [FFmpeg-Builds](https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/tag/autobuild-2026-10-31-12-59). The SHA256 is pinned in `tools/fetch-ffmpeg.ps1`.
- **License:** GNU Lesser General Public License version 3 (the build is configured with `--enable-version3` and without GPL components; libx264 and libx265 are not included). The license text is in `ffmpeg/LICENSE.txt`.
- **Source:** the FFmpeg source for this build is at [github.com/FFmpeg/FFmpeg](https://github.com/FFmpeg/FFmpeg), release branch 9.0, commit `7adfbe40dc`. The build scripts are at [github.com/FFmpeg-Builds/FFmpeg-Builds](https://github.com/FFmpeg-Builds/FFmpeg-Builds).
- You may replace the FFmpeg files in the `ffmpeg` folder with your own build of the same interface.
- Grably encodes only with encoders that come with Windows or the graphics driver (Media Foundation, Intel Quick Sync, NVIDIA NVENC, AMD AMF). It does not use OpenH264: Cisco's patent licence covers only Cisco's own OpenH264 binaries.

> **Open before the first public release (M10).** The FFmpeg-Builds package also contains about 40 other libraries (for example dav1d, libvpx, aom, LAME, libass, freetype, harfbuzz, libxml2, OpenH264, SRT and ZeroMQ) under BSD, MIT, Apache, ISC, FreeType, LGPL and MPL licences. Grably does not use them, but shipping them requires their notices and, for the LGPL and MPL ones, their source. The plan is to replace this package with a minimal LGPL build containing only what Grably uses (raw video input, Matroska and MP4, the scale and format filters, the hardware and Media Foundation encoders, AAC for M7, GIF for M9), which also cuts about 155 MB of DLLs. Until then this file is incomplete and the build must not be distributed.

## NuGet packages

| Package | License |
|---|---|
| CommunityToolkit.Mvvm | MIT |
| H.NotifyIcon.Wpf | MIT |
| Microsoft.Data.Sqlite, Microsoft.Extensions.* | MIT |
| Microsoft.Windows.CsWin32 (build time only) | MIT |
| Serilog, Serilog.Extensions.Hosting, Serilog.Sinks.File | Apache-2.0 |
| VirtualizingWrapPanel | MIT |
| Vortice.Direct2D1, Vortice.Direct3D11 (and Vortice.DXGI, Vortice.DirectX, Vortice.Mathematics) | MIT |
| WPF-UI | MIT |
