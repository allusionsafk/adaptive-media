# Native Zero-Disk Dolby Vision Profile 7 Playback Design

## Status and baseline

This design is approved for implementation on branch
`feature/native-zero-disk-dv-p7`, based on
`9de3f8efb271f6643087bd9471a1f5c9dfe03376`.

The recorded baseline is 339 assertions: 220 Adaptive Media, 20 settings, 58
Dolby Vision semantic, and 41 Dolby Vision real-execution assertions. Packaging,
historical reconstruction, repository layout, PowerShell syntax, application
build, and installer compilation also pass. The application build has zero
warnings and zero errors.

## Goal

Prove whether a pinned post-FEL mpv/libplacebo/FFmpeg runtime can directly play
the immutable authored Profile 7 Matroska source on Windows, with bounded RAM and
VRAM and no media-sized playback writes. Integrate the smallest experimental
Adaptive Media lane only if the direct proof establishes an honest, observable
runtime contract.

Normal playback must read the original container directly. The existing
transactional Profile 7 to Profile 8.1 converter remains unchanged and is
conceptually a Compatibility Export feature, not playback preparation.

## Decision hierarchy

1. Prove exact upstream and binary capabilities before application changes.
2. Classify the authored source from multiple independent evidence sources.
3. Prove direct mpv playback and FEL contribution independently of Adaptive
   Media.
4. Measure storage and memory behaviour through launch, sustained playback,
   pause/resume, seeking, and exit.
5. Integrate only runtime state that can be observed truthfully.

A binary that opens the file but does not prove enhancement-layer decode,
BL/EL pairing, FEL composition, and deterministic picture contribution is not a
successful FEL result.

## Experimental runtime isolation

The post-FEL runtime is a side-by-side, content-addressed experimental asset. It
must not replace or mutate `C:\mpv`, `PATH`, file associations, the user's mpv
configuration, or Adaptive Media's stable dependency provisioner.

Every direct experiment launches the pinned executable by absolute path with:

- `--no-config` or a dedicated experimental config directory;
- an isolated working directory and explicit log destination;
- isolated cache, shader-cache, watch-later, and state locations where the
  runtime exposes them;
- a controlled environment recorded with the exact argument vector;
- no helper process unless the experiment explicitly records and measures it.

The runtime manifest records archive URL, SHA-256, archive filename, executable
hash, mpv commit/version, FFmpeg version/configuration, libplacebo version/API,
acquisition time, and feature probes. A reputable existing Windows build is
preferred. A custom build is justified only after an exact missing dependency or
feature is demonstrated.

## Source classification

The authored source is opened read-only and never modified. Classification
combines:

- container and stream metadata from ffprobe/FFmpeg;
- Dolby Vision RPU and enhancement-layer analysis from pinned dovi_tool;
- post-FEL mpv demux/decoder diagnostics.

The report distinguishes Profile 7, compatibility ID, HDR10 base compatibility,
BL, RPU, EL, stream organisation, and interleaved versus dependent/layered
representation. EL presence alone does not classify FEL. FEL classification
requires evidence of non-trivial enhancement-layer/NLQ picture contribution.

## Direct playback proof

The experimental harness launches the original Matroska twice at identical,
keyframe-safe authored timestamps:

- enhancement layer enabled;
- enhancement layer disabled.

Both runs use `gpu-next` and capture the exact runtime, configuration, command,
environment, stdout/stderr, IPC state, frame timing, dropped frames, and decoder
selection. The enabled result must prove the EL decoder, BL/EL pairing, RPU
state, and libplacebo FEL composition path. Decoder requests and observed
decoder state are recorded separately.

Deterministic frame captures use the same seek mode, timestamp, output colour
settings, renderer, and output format. Pixel hashes and an objective difference
report establish whether enabling the enhancement layer changes rendered pixels
at a timestamp where non-trivial FEL contribution is present. Screenshots or
visual judgement alone are insufficient.

## Storage proof

The harness snapshots and monitors attributable filesystem writes for the
experimental mpv process, any explicitly used helper, and later Adaptive Media.
Measurement covers launch, sustained playback, pause/resume, repeated seeking,
and exit.

Results are separated into:

- bounded control state: logs, configuration, shader cache, watch-later, and
  diagnostics, reported by exact path and byte count;
- media payload state: extracted video, converted video, temporary media
  containers, progressive caches, and shadow media files.

The success criterion for media payload state is exactly zero files and zero
bytes. Bounded control state may be a few megabytes if every path and maximum
size is reported and it does not grow with movie duration.

## Memory, GPU, and seek proof

Measurements sample process CPU and working set plus NVIDIA GPU utilisation,
dedicated VRAM, shared memory where observable, and active decoder engines.
Sampling covers startup, steady playback, pause/resume, and a seek schedule with
short forward/backward, large, near-start, middle, near-end, and rapid repeated
seeks.

The report records average and peak values, startup latency, recovery latency,
frame-time peaks, decoder and output drops, reset/re-pair evidence, and whether
memory returns to a bounded range after seeks. A short fixture and the full title
are compared where practical to support the invariant that working memory scales
with resolution and queue depth, not movie duration or file size.

## Requested and observed pipeline model

The application model has explicit requested and observed records.

Requested state contains source profile/classification, FEL request, runtime
identity, renderer, GPU API/context, and hardware-decode request.

Observed state contains BL decoder, EL decoder, EL active state, BL/EL pairing,
FEL composition, RPU state, renderer, GPU API/context, hardware surfaces,
fallback reason, media-payload scratch bytes, bounded-state bytes, RAM peak,
VRAM peak, drops, and timing observations. Unknown values remain unknown; they
are never promoted to false or active.

Structured mpv IPC properties are preferred. Version-pinned diagnostic parsing
may support the laboratory proof, but the application must not permanently rely
on fragile unrestricted regexes. If upstream exposes insufficient structured
truth, a tiny diagnostic property patch may be maintained side-by-side and
proposed upstream. No broad mpv fork is permitted.

## Adaptive Media integration

Integration is conditional on successful direct playback proof. The minimum
lane consists of:

- explicit experimental Profile 7 native-playback selection;
- absolute selection of the pinned side-by-side runtime;
- original-MKV playback with no conversion executor or media scratch path;
- typed requested and observed state in diagnostics;
- visible BL-only, software-decode, missing-EL, or unknown-composition fallback;
- cancellation and exit cleanup restricted to bounded diagnostic state;
- preservation of audio, subtitles, chapters, and attachments through the
  original Matroska playback path.

Stable playback remains available. Stable runtime resolution and provisioning
are unchanged. No broad UI redesign is included; a minimal developer-facing
toggle or explicit experimental profile is sufficient.

## Failure policy

Direct proof stops before integration when the runtime lacks required FFmpeg,
libplacebo, or mpv support; the exact deficiency and confirming evidence are
reported. A MEL source or an unproven non-trivial FEL contribution cannot certify
FEL. Hardware-decoder failure may fall back for investigation, but fallback is
explicit and cannot be reported as full FEL.

Performance-adaptive fallback is design-only for this milestone. Sustained
deadline failure signals may be recorded, but automatic product policy is not
implemented.

## Verification

Production changes follow test-first development. Focused gates cover source
classification, isolated runtime selection, requested-versus-observed state,
truthful fallback, absence of conversion calls and media scratch paths, source
immutability, cleanup, and stable-runtime isolation.

The complete existing validation suite, application build, installer compile,
repository safety gates, direct runtime experiments, storage/memory measurements,
and independent review must pass before completion. Results that remain
hardware- or upstream-limited are reported as blockers rather than inferred.

## Explicit exclusions

No full Profile 7 to Profile 8.1 conversion, RAM disk, media-sized cache,
provisioner replacement, stable mpv replacement, Profile 5 implementation,
NVENC export, AI enhancement, Shadow Stream implementation, installer redesign,
release, tag, or publication is part of this milestone.

Profile 5 reuse and a common future GPU frame graph may be documented only as
architecture notes.
