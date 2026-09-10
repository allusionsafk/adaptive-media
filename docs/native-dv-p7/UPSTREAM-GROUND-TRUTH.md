# Native Dolby Vision upstream ground truth

Status: runtime selected and capability-probed; renderer composition remains unproven.

## Pinned upstream revisions

The source snapshots under ignored `.artifacts/native-dv/upstream/` are clean and resolve to:

| Project | Revision | Relevant floor |
| --- | --- | --- |
| mpv | `7e4cb538a3f30d25920ad8e87ba6571540fb729f` | 196 commits after FEL merge `99b4c12cccb4d8d3f72b41944cb6c640e2156650` |
| libplacebo | `3330a515d62139259c26239014f286e233bd3a5c` | 7.371.0 / API 371 |
| FFmpeg reference head | `fd7c73d01e976d2e332e85862ab63ab608710834` | Current source-chain reference captured 2026-09-10 |
| FFmpeg embedded in candidate | `903325e279b67156c3aa1f06ec5cb2378d9d004d` | `dovi_split`, layered-video grouping, and Matroska EL grouping present |

The official mpv comparison identifies `99b4c12` as the merge base and the selected mpv revision as 196 commits ahead. The newest official stable tag available during selection was v0.41.0; the FEL change is merged for the not-yet-tagged v0.42.0 milestone. Sources: [mpv FEL pull request](https://github.com/mpv-player/mpv/pull/17932), [official commit comparison](https://github.com/mpv-player/mpv/compare/99b4c12...7e4cb538a3f30d25920ad8e87ba6571540fb729f), and [mpv releases](https://github.com/mpv-player/mpv/releases).

## Source-chain facts

These are confirmed from the pinned local source trees, not inferred from a file opening successfully:

- `demux/dovi_split.c:58-131` requests FFmpeg's `dovi_split` bitstream filter, creates the virtual dependent EL stream, and reports the BL/EL stream identities. `demux/dovi_split.c:140-144` flushes the splitter on reset.
- `filters/f_enhancement_pair.c:101-180` inherits EL Dolby Vision metadata, pairs frames by PTS, attaches the matched EL, and clears the attachment when unavailable. `filters/f_output_chain.c:412-436` inserts `el_pair` when an EL stream exists.
- `video/filter/vf_format.c:194-198` discards the attached EL and marks it disabled when requested; `video/filter/vf_format.c:283,305` exposes `enhancement-layer` and defaults it on.
- `video/out/vo_gpu_next.c:860,1055-1088` maps/uploads the attached EL and supplies it as libplacebo's enhancement frame.
- libplacebo API 366 added FEL NLQ metadata, API 367 added `pl_frame.enhancement_layer`, and API 369 added `pl_color_decode_args.enhancement_layer`; the selected API is 371.
- `src/include/libplacebo/colorspace.h:152-161` says NLQ is the Profile 7 FEL contribution and is inactive for MEL. `src/renderer.c:2063-2079` gates EL sampling/composition on Dolby Vision plus `nlq_active`. `src/shaders/colorspace.c:341-346` calls `sh_dovi_compose_nlq` only under that gate.
- FFmpeg `903325e279...` registers `ff_dovi_split_bsf`, exposes layered-video `el_index`, and groups the Matroska base and enhancement layers in `libavformat/matroskadec.c`.

Consequently, proof has three distinct boundaries: splitter/decoder creation, exact-PTS pairing, and gpu-next/libplacebo NLQ composition. The first two do not imply the third.

## Selected Windows runtime

The selected artifact is zhongfly's `mpv-x86_64-20260909-git-7e4cb538a3.7z`, published 2026-09-09. Its publisher metadata reports 32,686,070 bytes and SHA-256 `b84ab4721365118925c052c87b4e9c940444ef266be3ee7999ddcb847c11a7c5`; the downloaded archive matches both. It was extracted only beneath `.artifacts/native-dv/runtime/<sha256>/` and no installer was run. Sources: [release](https://github.com/zhongfly/mpv-winbuild/releases/tag/2026-09-09-7e4cb538a3), [build details](https://github.com/zhongfly/mpv-winbuild/actions/runs/34348566908), and [FFmpeg embedded revision](https://github.com/FFmpeg/FFmpeg/commit/903325e279b67156c3aa1f06ec5cb2378d9d004d).

Binary metadata confirms:

```text
mpv v0.41.0-1042-g7e4cb538a (built 2026-09-09 12:25:50 UTC)
libplacebo v7.371.0 (v7.360.0-124-g3330a51-dirty)
FFmpeg N-126482-g903325e27
libavcodec 63.11.101; libavformat 63.6.100
```

The build lists gpu-next, Vulkan, D3D11, CUDA interop, NVDEC HEVC, and D3D11VA HEVC. `--vf=format=help` exposes `enhancement-layer=yes|no`. This satisfies the dependency and compile-feature floors, so a custom build is not justified.

## Isolated capability probe

The probe used the absolute experimental `mpv.com` console launcher, not `PATH` resolution. Its effective argument vector was:

```text
--no-config
--config-dir=<run>/state
--demuxer-cache-dir=<run>/state
--gpu-shader-cache-dir=<run>/state
--gpu-shader-cache=no
--watch-later-dir=<run>/state
--save-position-on-quit=no
--cache=no
--cache-on-disk=no
--audio=no
--sub=no
--hwdec=no
--vo=null
--frames=1
--vf=format=enhancement-layer=yes
--msg-level=all=v
-- <authored-source>
```

`TEMP` and `TMP` were both set to `<run>/temp`. The process reported:

```text
Dolby Vision Profile 7 splitter: BL stream 0, virtual EL stream 1 (dependent_track).
Opening decoder hevc
Opening decoder hevc
[vf] [el_pair] 3840x2160 yuv420p10 dolbyvision/bt.2020/pq/limited/display
```

This is evidence that the packaged FFmpeg splitter, two decoder instances, and mpv pairing filter are active. Because the probe deliberately used `vo=null`, it is not renderer evidence and does not certify FEL composition.

The isolated probe directories contained zero files after exit: bounded runtime-state bytes `0`; media payload/temp/cache bytes `0`.

## Stable-runtime isolation

Before the probe, `C:\mpv\mpv.exe` was version `0.41.0-1011-g182fa6ca4`, 123,145,216 bytes, last written `2026-08-28T00:52:38.1690232Z`, with SHA-256 `20f742a74f07b641a272ce5c521937c74aae3791b394fb069fe548f67d32b715`. Although `C:\mpv` is already on the process `PATH`, every experiment names the side-by-side binary by absolute path. No file in `C:\mpv`, no `PATH` value, no association, no global config, and no dependency provisioner was changed.

The machine-readable identity and feature results are in `runtime-manifest.json`. Later hardware and A/B runs must re-check the stable executable fingerprint and enumerate all experiment filesystem deltas.
