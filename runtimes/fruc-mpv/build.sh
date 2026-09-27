#!/usr/bin/env bash
# Builds DemiMedia's experimental Generated Motion runtime inside an MSYS2 UCRT64
# shell: FFmpeg 9.0.2 with the nvofruc filter, shared, installed under $PREFIX.
# The mpv executable and its other libraries come from the pinned MSYS2 packages
# (see runtime.lock); Stage-FrucRuntime.ps1 assembles the app-private runtime.
#
# Usage: build.sh <ffmpeg-9.0.2.tar.xz> <work-dir> <prefix>
set -euo pipefail
tarball=$(cygpath -u "$1"); work=$(cygpath -u "$2"); prefix=$(cygpath -u "$3")
here=$(cd "$(dirname "$0")" && pwd)
expected=8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e
echo "$expected *$tarball" | sha256sum -c -

mkdir -p "$work"
rm -rf "$work/ffmpeg-9.0.2"
tar -xJf "$tarball" -C "$work"
src="$work/ffmpeg-9.0.2"
# As MSYS2 does: avoid the C++20 <version> header clash.
mv "$src/VERSION" "$src/VERSION.txt"

cp "$here/ffmpeg/vf_nvofruc.c" "$src/libavfilter/vf_nvofruc.c"
sed -i 's/^OBJS-\$(CONFIG_SCALE_D3D11_FILTER).*/&\nOBJS-$(CONFIG_NVOFRUC_FILTER)                += vf_nvofruc.o/' "$src/libavfilter/Makefile"
sed -i 's/^extern const FFFilter ff_vf_scale_d3d11;/&\nextern const FFFilter ff_vf_nvofruc;/' "$src/libavfilter/allfilters.c"
sed -i 's/^scale_d3d11_filter_deps="d3d11va"/&\nnvofruc_filter_deps="d3d11va"/' "$src/configure"
grep -q vf_nvofruc.o "$src/libavfilter/Makefile"
grep -q ff_vf_nvofruc "$src/libavfilter/allfilters.c"
grep -q nvofruc_filter_deps "$src/configure"

mkdir -p "$work/build"
cd "$work/build"
"$src/configure" \
  --prefix="$prefix" \
  --target-os=mingw32 --arch=x86_64 \
  --enable-shared --disable-static \
  --disable-debug --disable-doc --disable-programs \
  --enable-gpl --enable-version3 \
  --enable-d3d11va --enable-dxva2 \
  --enable-libdav1d --enable-zlib --enable-iconv \
  --enable-runtime-cpudetect \
  --extra-version=demimedia-fruc1 >configure.log
grep -q "#define CONFIG_NVOFRUC_FILTER 1" config_components.h || { echo "nvofruc filter disabled by configure" >&2; exit 1; }
make -j"$(nproc)" >make.log 2>&1 || { tail -40 make.log; exit 1; }
make install >install.log 2>&1
echo "BUILT $(ls "$prefix"/bin/*.dll | wc -l) DLLs into $prefix"
