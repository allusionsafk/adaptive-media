#!/usr/bin/env bash
# Assembles the app-private Generated Motion runtime from the pinned MSYS2 mpv and
# the FFmpeg built by build.sh. The runtime directory is self-contained: mpv.exe,
# our FFmpeg DLLs, and the transitive closure of the remaining UCRT64 DLLs it loads
# (resolved app-directory first, as Windows does).
#
# Usage: stage.sh <ffmpeg-prefix> <out-dir>
set -euo pipefail
prefix=$(cygpath -u "$1"); out=$(cygpath -u "$2")
rm -rf "$out"; mkdir -p "$out"
cp /ucrt64/bin/mpv.exe /ucrt64/bin/mpv.com "$out/"
cp "$prefix"/bin/*.dll "$out/"
for pass in 1 2 3 4 5 6 7 8; do
  added=0
  for f in "$out"/*.exe "$out"/*.dll; do
    while read -r dll; do
      [ -e "$out/$dll" ] && continue
      [ -e "/ucrt64/bin/$dll" ] || continue
      cp "/ucrt64/bin/$dll" "$out/"; added=$((added + 1))
    done < <(cd "$out" && PATH="$out:/ucrt64/bin:/usr/bin" ldd "$f" 2>/dev/null | awk '$3 ~ /ucrt64\/bin/ {print $1}')
  done
  [ "$added" -eq 0 ] && break
done
# Nothing may still resolve outside the runtime except Windows itself.
leak=$(cd "$out" && for f in *.exe *.dll; do PATH="$out:/usr/bin" ldd "$f" 2>/dev/null; done | awk '$3 ~ /(ucrt64|usr\/bin|home)/' | sort -u)
[ -z "$leak" ] || { echo "Unresolved non-system dependencies:"; echo "$leak"; exit 1; }
{ pacman -Q mingw-w64-ucrt-x86_64-mpv mingw-w64-ucrt-x86_64-libplacebo; echo "ffmpeg 9.0.2 + nvofruc (build.sh)"; } > "$out/RUNTIME-SOURCES.txt"
(cd "$out" && sha256sum -- * > ../runtime.sha256 && mv ../runtime.sha256 ./SHA256SUMS)
echo "STAGED $(ls "$out" | wc -l) files, $(du -sh "$out" | cut -f1)"
