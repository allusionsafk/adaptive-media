# Native Dolby Vision experiment protocol

## Scope and invariants

The experiment reads the authored Matroska source directly. It must never invoke the compatibility exporter, extract BL/EL video, encode video, create a temporary media container, or create a cache whose growth follows the media payload. The only permitted outputs are bounded metadata, logs, measurements, and one lossless frame capture per comparison run.

Every run:

1. resolves the pinned console launcher from `runtime-manifest.json` and verifies its byte length and SHA-256;
2. rejects any executable beneath `C:\mpv`;
3. records the exact argument array and environment before launch;
4. uses `--no-config`, explicit run-local state/cache/watch-later/log paths, disk cache off, shader cache off, and save-position off;
5. overrides `TEMP` and `TMP` only for the child processes and points both beneath the run directory;
6. records first/middle/last 16 MiB source sentinels, length, and UTC timestamp before work and after each phase;
7. fingerprints `C:\mpv\mpv.exe` before and after without executing or modifying it; and
8. always finalizes IPC, source-identity, runtime-log, filesystem-delta, and result evidence after failure or cancellation.

The runner uses `System.Diagnostics.ProcessStartInfo.ArgumentList`; no runtime or helper command is assembled as a shell string.

## Source classification

`ffprobe` reads container and stream metadata as JSON. FFmpeg then stream-copies a bounded interval of the selected video track to dovi_tool through an anonymous pipe. No HEVC is written to disk. dovi_tool stops after 120 pictures and writes only the RPU metadata stream under `state/classification.rpu.bin`; `dovi_tool info --summary` supplies the documented `Profile: 7 (MEL|FEL)` result.

An EL-present flag alone leaves `MelFel=Unknown`. A runtime `sh_dovi_compose_nlq` shader plus an enhancement-layer GPU stage is independent FEL/NLQ evidence. A dovi_tool/runtime contradiction produces `Unknown`, never a manufactured FEL result.

## Playback and capture

The two comparison runs must differ only in run identifiers, isolated output paths/pipe names, and:

```text
--vf=format=enhancement-layer=yes
--vf=format=enhancement-layer=no
```

The authored source, timestamp, GPU backend/context, hardware-decoder request, 1280x720 window geometry, BT.709/BT.1886 target, bt.2446a tone mapping, and high-bit-depth PNG settings remain identical. The runner starts paused, performs `seek <timestamp> absolute+exact`, requires `seeking=false` and an observed `time-pos` within 125 ms, then issues `screenshot-to-file <path> window`. `window` is intentional: it captures gpu-next's rendered result rather than the pre-VO decoded frame.

`Compare-NativeDvCaptures.ps1` decodes non-interlaced 8/16-bit RGB/RGBA PNG samples without quantizing 16-bit captures. It records hashes, dimensions, changed-pixel count/fraction, maximum native channel delta, mean absolute channel error, normalized error, and the bounding box of all changed pixels.

## Runtime truth

Structured IPC snapshots are primary for requested versus observed runtime, including `hwdec-current`, `gpu-api`, `gpu-context`, filter chain, video parameters, track list, time, pause/seeking state, and frame/drop counters. The narrowly pinned diagnostic adapter additionally reduces these exact log facts:

- Profile 7 splitter and virtual dependent EL stream;
- two HEVC decoder openings/selections;
- `el_pair` filter activation;
- libplacebo API;
- compiled `sh_dovi_compose_nlq` marker;
- libplacebo's timed enhancement-layer GPU stage; and
- hardware-decode success or software/device-creation fallback.

The log adapter is version-bound to the runtime manifest. It cannot turn a successful open or playback exit into a composition claim.

## Phase schedule and measurements

Each evidence run covers launch, deterministic capture, sustained playback, pause, resume, five forward/backward exact seeks, and IPC exit. `metrics.csv` samples process cumulative CPU, working set, private/page bytes, global NVIDIA GPU utilization, decoder utilization, and global used/free VRAM. Because NVIDIA's WDDM query does not expose per-process VRAM, that field is explicitly unavailable rather than estimated. Early/late samples and post-seek samples are compared for boundedness.

For the final pair, Windows File I/O ETW/WPR is run around the experiment and reduced by PID/image/path. This catches writes outside the intentionally isolated run directory. Harness-created evidence is reported separately from mpv/helper writes. ETL is diagnostic state, not media payload, and its exact size/path is reported.

## Decision gate

`supported` requires all of:

- authored source classified FEL with non-trivial NLQ evidence;
- runtime splitter, two decoder instances, pairing, and libplacebo NLQ composition evidence;
- FEL-on versus FEL-off captures at the same observed timestamp with nonzero objective pixel differences;
- zero extracted/converted video, temporary media container, progressively growing media cache, or shadow media file;
- unchanged source and stable-runtime fingerprints;
- bounded RAM/VRAM behaviour through sustained play and seeks; and
- successful launch, pause/resume, seek recovery, and exit.

Missing structured or objective evidence yields `unknown`; an observed incompatible path yields `unsupported`. Neither outcome authorizes product integration.
