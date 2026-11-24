#!/usr/bin/env bash
# Builds the ffmpeg that ships with Grably: LGPL only, for Windows x64, with just what Grably uses. Cross-compiled
# with mingw-w64 on Ubuntu 24.04; tools/ffmpeg/build.ps1 runs it in Docker, the release workflow in a container job.
#
# Every source is pinned (a SHA256 for tarballs, a commit for git tags), so the same script gives the same build.
# The extra libraries are all permissively licensed: zlib (zlib licence; PNG needs it), libvpl (MIT; Intel Quick
# Sync), nv-codec-headers (MIT; NVIDIA NVENC, loaded at run time) and the AMF headers (MIT; AMD, loaded at run time).
# The output folder gets ffmpeg.exe, ffprobe.exe, the DLLs, the licences, BUILD.txt (versions and the configure
# line) and VERSION.txt.
#
# Usage: build.sh <output folder>
set -euo pipefail

OUT="${1:?usage: build.sh <output folder>}"
SCRIPT_SHA256="$(sha256sum "$0" | cut -d' ' -f1)"
NAME="grably-ffmpeg-9.0.2-1"

FFMPEG_URL="https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz"
FFMPEG_SHA256="8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e"
ZLIB_URL="https://github.com/madler/zlib/releases/download/v1.3.1/zlib-1.3.1.tar.gz"
ZLIB_SHA256="9a93b2b7dfdac77ceba5a558a580e74667dd6fede4585b91eefb60f03b72df23"
LIBVPL_REPO="https://github.com/intel/libvpl.git"
LIBVPL_TAG="v2.15.0"
LIBVPL_COMMIT="c45b5d786bf7cdabbe49ff1bab78693ad78feb78"
NVHEADERS_REPO="https://github.com/FFmpeg/nv-codec-headers.git"
NVHEADERS_TAG="n12.2.72.0" # NVENC API 12.2: NVIDIA drivers from 550 (newer headers would need newer drivers)
NVHEADERS_COMMIT="c69278340ab1d5559c7d7bf0edf615dc33ddbba7"
AMF_REPO="https://github.com/GPUOpen-LibrariesAndSDKs/AMF.git"
AMF_TAG="v1.5.2" # ffmpeg 9 needs AMF headers 1.5.2 or newer
AMF_COMMIT="eadd00804d5f7e5cd8c85d540073198312870776"

TRIPLE="x86_64-w64-mingw32"
DEPS="/opt/deps"
WORK="/tmp/ffbuild"
JOBS="$(nproc)"

# What Grably asks of ffmpeg (FfmpegArguments, VideoEncoders, the DevCli and the tests). Anything new must be added here.
ENCODERS="h264_nvenc,hevc_nvenc,h264_qsv,hevc_qsv,h264_amf,hevc_amf,h264_mf,hevc_mf,aac,mpeg4,png,bmp,gif,rawvideo,wrapped_avframe,pcm_s16le,pcm_f32le"
DECODERS="h264,hevc,mpeg4,aac,gif,png,bmp,rawvideo,wrapped_avframe,pcm_f32le,pcm_s16le"
MUXERS="mp4,mov,matroska,gif,image2,image2pipe,null,rawvideo"
DEMUXERS="mov,matroska,gif,image2,rawvideo,pcm_f32le,wav"
PARSERS="h264,hevc,aac,mpeg4video,png,gif,bmp"
BSFS="aac_adtstoasc,extract_extradata,h264_mp4toannexb,hevc_mp4toannexb,null"
FILTERS="buffer,buffersink,abuffer,abuffersink,null,anull,format,aformat,scale,aresample,trim,atrim,setpts,asetpts,concat,crop,fps,split,tile,palettegen,paletteuse,showwavespic,amix,alimiter,apad,volume,showinfo,silencedetect,hflip,vflip,transpose,color,testsrc2,sine"

log() { printf '\n== %s\n' "$*"; }

fetch() { # url sha256 file
    curl -fsSL --retry 3 -o "$3" "$1"
    echo "$2  $3" | sha256sum -c --quiet -
}

clone() { # repo tag commit folder
    git -c advice.detachedHead=false clone -q --depth 1 --branch "$2" "$1" "$4"
    local actual
    actual="$(git -C "$4" rev-parse HEAD)"
    [ "$actual" = "$3" ] || { echo "$1 $2 is at $actual, expected $3" >&2; exit 1; }
}

log "Toolchain"
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq --no-install-recommends \
    ca-certificates curl git xz-utils make pkg-config nasm cmake gcc libc6-dev \
    gcc-mingw-w64-x86-64-posix g++-mingw-w64-x86-64-posix binutils-mingw-w64-x86-64 mingw-w64-x86-64-dev >/dev/null
# The posix thread model, which libvpl's C++ needs; its runtime is linked in statically below.
update-alternatives --set "$TRIPLE-gcc" "/usr/bin/$TRIPLE-gcc-posix" >/dev/null
update-alternatives --set "$TRIPLE-g++" "/usr/bin/$TRIPLE-g++-posix" >/dev/null

rm -rf "$WORK" && mkdir -p "$WORK" "$DEPS/include" "$DEPS/lib/pkgconfig"
cd "$WORK"

log "zlib"
fetch "$ZLIB_URL" "$ZLIB_SHA256" zlib.tar.gz
tar xf zlib.tar.gz
make -s -C zlib-1.3.1 -f win32/Makefile.gcc PREFIX="$TRIPLE-" -j"$JOBS" libz.a >/dev/null
cp zlib-1.3.1/libz.a "$DEPS/lib/" && cp zlib-1.3.1/zlib.h zlib-1.3.1/zconf.h "$DEPS/include/"

log "nv-codec-headers"
clone "$NVHEADERS_REPO" "$NVHEADERS_TAG" "$NVHEADERS_COMMIT" nv-codec-headers
make -s -C nv-codec-headers PREFIX="$DEPS" install >/dev/null

log "AMF headers"
git -c advice.detachedHead=false clone -q --depth 1 --branch "$AMF_TAG" --filter=blob:none --sparse "$AMF_REPO" amf
git -C amf sparse-checkout set amf/public/include
[ "$(git -C amf rev-parse HEAD)" = "$AMF_COMMIT" ] || { echo "AMF $AMF_TAG moved" >&2; exit 1; }
mkdir -p "$DEPS/include/AMF" && cp -r amf/amf/public/include/* "$DEPS/include/AMF/"

log "libvpl"
clone "$LIBVPL_REPO" "$LIBVPL_TAG" "$LIBVPL_COMMIT" libvpl
cmake -S libvpl -B libvpl/build -DCMAKE_SYSTEM_NAME=Windows -DCMAKE_C_COMPILER="$TRIPLE-gcc" -DCMAKE_CXX_COMPILER="$TRIPLE-g++" \
    -DCMAKE_RC_COMPILER="$TRIPLE-windres" -DCMAKE_FIND_ROOT_PATH="/usr/$TRIPLE" -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX="$DEPS" -DCMAKE_INSTALL_LIBDIR=lib -DBUILD_SHARED_LIBS=OFF -DBUILD_TESTS=OFF -DBUILD_EXAMPLES=OFF \
    -DINSTALL_EXAMPLES=OFF -DBUILD_EXPERIMENTAL=OFF -DBUILD_TOOLS=OFF -DENABLE_WARNING_AS_ERROR=OFF >/dev/null
cmake --build libvpl/build -j"$JOBS" >/dev/null
cmake --install libvpl/build >/dev/null
# The dispatcher is C++: whatever links it needs the C++ runtime too, linked in statically (by file name, since the
# compiler would otherwise pick the DLL's import library) so no MinGW runtime DLL has to ship.
sed -i -e 's/ *-lstdc++//g' -e 's/^Libs.private:.*/& -l:libstdc++.a -l:libwinpthread.a/' "$DEPS/lib/pkgconfig/vpl.pc"
grep -q '^Libs.private:' "$DEPS/lib/pkgconfig/vpl.pc" || echo 'Libs.private: -l:libstdc++.a -l:libwinpthread.a' >> "$DEPS/lib/pkgconfig/vpl.pc"
cat "$DEPS/lib/pkgconfig/vpl.pc"

log "ffmpeg"
fetch "$FFMPEG_URL" "$FFMPEG_SHA256" ffmpeg.tar.xz
tar xf ffmpeg.tar.xz
cd ffmpeg-9.0.2
export PKG_CONFIG_LIBDIR="$DEPS/lib/pkgconfig"
CONFIGURE=(
    --prefix="$WORK/install" --target-os=mingw32 --arch=x86_64 --cross-prefix="$TRIPLE-" --enable-cross-compile
    --pkg-config=pkg-config --pkg-config-flags=--static
    --extra-cflags="-I$DEPS/include" --extra-ldflags="-L$DEPS/lib -static-libgcc"
    --enable-shared --disable-static --disable-debug --disable-doc
    --disable-gpl --disable-nonfree --disable-network --disable-autodetect --disable-everything
    --disable-programs --enable-ffmpeg --enable-ffprobe
    --enable-w32threads --enable-zlib --enable-mediafoundation --enable-d3d11va --enable-dxva2
    --enable-ffnvcodec --enable-nvenc --enable-amf --enable-libvpl
    --enable-encoder="$ENCODERS" --enable-decoder="$DECODERS" --enable-muxer="$MUXERS" --enable-demuxer="$DEMUXERS"
    --enable-parser="$PARSERS" --enable-bsf="$BSFS" --enable-filter="$FILTERS"
    --enable-protocol=file,pipe --enable-indev=lavfi
)
./configure "${CONFIGURE[@]}" > "$WORK/configure.log" || { tail -40 ffbuild/config.log >&2; exit 1; }
make -s -j"$JOBS" >/dev/null
make -s install >/dev/null

log "Collecting"
rm -rf "$OUT"/* && mkdir -p "$OUT/licenses"
cp "$WORK"/install/bin/*.exe "$WORK"/install/bin/*.dll "$OUT/"
"$TRIPLE-strip" --strip-unneeded "$OUT"/*.exe "$OUT"/*.dll
cp COPYING.LGPLv2.1 "$OUT/licenses/ffmpeg-LGPL-2.1.txt"
cp ../zlib-1.3.1/LICENSE "$OUT/licenses/zlib.txt"
cp ../libvpl/LICENSE "$OUT/licenses/libvpl-MIT.txt"
cp ../amf/LICENSE.txt "$OUT/licenses/AMF-MIT.txt" 2>/dev/null || sed -n '1,/\*\//p' "$DEPS/include/AMF/core/Version.h" > "$OUT/licenses/AMF-MIT.txt"
sed -n '1,/\*\//p' "$DEPS/include/ffnvcodec/nvEncodeAPI.h" > "$OUT/licenses/nv-codec-headers-MIT.txt"
# The MinGW-w64 runtime (CRT start-up code, winpthreads) is linked in; its notices cover those parts.
cp /usr/share/doc/mingw-w64-common/copyright "$OUT/licenses/mingw-w64.txt"

# Nothing but Windows itself and ffmpeg's own DLLs may be needed at run time.
for file in "$OUT"/*.exe "$OUT"/*.dll; do
    "$TRIPLE-objdump" -p "$file" | sed -n 's/^\s*DLL Name: //p'
done | sort -u > "$WORK/imports.txt"
own="$(cd "$OUT" && ls ./*.dll | sed -e 's#^\./##' -e 's#\.dll$##' | tr '\n' '|')"
if grep -viE "^(${own}kernel32|user32|gdi32|advapi32|ole32|oleaut32|shell32|bcrypt|ws2_32|mfplat|mf|mfuuid|strmiids|d3d11|dxgi|d3d9|dxva2|psapi|shlwapi|secur32|api-ms-win-[a-z0-9-]+|msvcrt|ucrtbase|ncrypt|crypt32|cfgmgr32|setupapi|version|winmm|propsys|uuid)\.dll$" "$WORK/imports.txt"; then
    echo "Unexpected DLLs above would have to ship too." >&2
    exit 1
fi

{
    echo "$NAME"
    echo "ffmpeg 9.0.2 ($FFMPEG_URL, SHA256 $FFMPEG_SHA256), LGPL 2.1 or later"
    echo "zlib 1.3.1 ($ZLIB_URL, SHA256 $ZLIB_SHA256)"
    echo "libvpl $LIBVPL_TAG ($LIBVPL_REPO, $LIBVPL_COMMIT)"
    echo "nv-codec-headers $NVHEADERS_TAG ($NVHEADERS_REPO, $NVHEADERS_COMMIT)"
    echo "AMF headers $AMF_TAG ($AMF_REPO, $AMF_COMMIT)"
    echo "Compiler: $("$TRIPLE-gcc" --version | head -1)"
    echo "Windows DLLs used: $(tr '\n' ' ' < "$WORK/imports.txt")"
    echo
    echo "configure ${CONFIGURE[*]}"
} > "$OUT/BUILD.txt"
# Which script made it: build.ps1 and pack.ps1 refuse a build from an older version of this script.
echo "$SCRIPT_SHA256" > "$OUT/BUILD-SCRIPT.sha256"
echo "$NAME" > "$OUT/VERSION.txt"
log "Done: $(ls "$OUT" | tr '\n' ' ')"
