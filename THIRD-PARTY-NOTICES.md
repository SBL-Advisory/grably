# Third-party notices

Grably is MIT licensed (see [LICENSE](LICENSE)). It ships and uses the following third-party software.

## FFmpeg

Grably records and exports video with [FFmpeg](https://ffmpeg.org), which it runs as a separate program (`ffmpeg.exe` and `ffprobe.exe` with their DLLs, in the `ffmpeg` folder next to `Grably.exe`). Grably does not link to FFmpeg.

- **Build:** Grably's own minimal build, `grably-ffmpeg-9.0.2-1`, made by [`tools/ffmpeg/build.sh`](tools/ffmpeg/build.sh) from the unmodified [FFmpeg 9.0.2 release](https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz). The SHA256 of the source and the exact `configure` line are in that script and in `ffmpeg/BUILD.txt` next to `ffmpeg.exe`.
  - It contains only what Grably uses: the Media Foundation, Intel Quick Sync, NVIDIA NVENC and AMD AMF encoders, AAC, GIF, PNG and BMP, Matroska and MP4, and the filters Grably asks for.
- **License:** GNU Lesser General Public License version 2.1 or later. The build is configured without GPL or non-free parts; libx264 and libx265 are not included. The license text is in `ffmpeg/licenses/ffmpeg-LGPL-2.1.txt`.
- **Source:** the exact FFmpeg source tarball is attached to every Grably release on GitHub, next to the installer. It is also at ffmpeg.org (link above). To rebuild it, run `tools/ffmpeg/build.ps1` from the same Grably version.
- You may replace the FFmpeg files in the `ffmpeg` folder with your own build of the same interface.
- **Encoders:** Grably encodes only with encoders that come with Windows or the graphics driver. It does not use OpenH264: Cisco's patent licence covers only Cisco's own OpenH264 binaries.

### Libraries inside that FFmpeg build

| Library | Use | License | Text |
|---|---|---|---|
| [zlib](https://zlib.net) 1.3.1 | PNG, linked in statically | zlib | `ffmpeg/licenses/zlib.txt` |
| [libvpl](https://github.com/intel/libvpl) 2.15.0 | Intel Quick Sync dispatcher, linked in statically | MIT | `ffmpeg/licenses/libvpl-MIT.txt` |
| [nv-codec-headers](https://github.com/FFmpeg/nv-codec-headers) 12.2.72.0 | NVIDIA NVENC interface (headers only; the driver is loaded at run time) | MIT | `ffmpeg/licenses/nv-codec-headers-MIT.txt` |
| [AMF](https://github.com/GPUOpen-LibrariesAndSDKs/AMF) headers 1.5.2 | AMD AMF interface (headers only; the driver is loaded at run time) | MIT | `ffmpeg/licenses/AMF-MIT.txt` |
| MinGW-w64 runtime (CRT start-up code, winpthreads, libgcc, libstdc++) | Compiler runtime, linked in statically | MinGW-w64 and winpthreads: ZPL, MIT and public domain parts; libgcc and libstdc++: GPL with the GCC Runtime Library Exception | `ffmpeg/licenses/mingw-w64.txt` |

## .NET

The installer includes the .NET 10 runtime and Windows Desktop runtime (self-contained), MIT licensed, © .NET Foundation and contributors. Its own third-party notices are at [github.com/dotnet/runtime](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT) and [github.com/dotnet/wpf](https://github.com/dotnet/wpf/blob/main/THIRD-PARTY-NOTICES.TXT).

## NuGet packages

| Package | License |
|---|---|
| CommunityToolkit.Mvvm | MIT |
| H.NotifyIcon.Wpf | MIT |
| Microsoft.Data.Sqlite, Microsoft.Extensions.* | MIT |
| SQLitePCLRaw (through Microsoft.Data.Sqlite) | Apache-2.0; SQLite itself is in the public domain |
| Microsoft.Windows.CsWin32 (build time only) | MIT |
| NAudio.Wasapi (and NAudio.Core) | MIT |
| Serilog, Serilog.Extensions.Hosting, Serilog.Sinks.File | Apache-2.0 |
| Velopack | MIT |
| VirtualizingWrapPanel | MIT |
| Vortice.Direct2D1, Vortice.Direct3D11 (and Vortice.DXGI, Vortice.DirectX, Vortice.Mathematics) | MIT |
| WPF-UI | MIT |

## Developer downloads

`tools/fetch-ffmpeg.ps1` downloads a larger ready-made LGPL build from [FFmpeg-Builds](https://github.com/FFmpeg-Builds/FFmpeg-Builds) for quick local development. It bundles about 40 more libraries whose notices are not listed here, so it is never packaged: `build/pack.ps1` refuses it.
