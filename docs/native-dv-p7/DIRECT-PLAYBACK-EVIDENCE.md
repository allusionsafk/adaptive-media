# Native Profile 7 direct playback evidence

Integration gate: **supported**.

The machine-readable record is `direct-playback-summary.json`, regenerated from
run artifacts by `scripts/New-NativeDvEvidenceSummary.ps1`. Every number below is
derived from that generator, not transcribed by hand. All seventeen gate checks
pass; feeding the generator a non-identical control run flips the gate to
`unsupported`, so the gate is load-bearing rather than decorative.

## Runtime this evidence belongs to

Everything below was produced by mpv `0.41.0-1042-g7e4cb538a`
(commit `7e4cb538a3f30d25920ad8e87ba6571540fb729f`), which was the pinned runtime
when these runs were made. On 2026-09-12 the shipped manifest moved to
`0.41.0-1044-g14f2d48cb`, whose diagnostic contract was re-verified against the
same authored source: Profile 7 splitter, two HEVC decoder instances,
`[vf] [el_pair]`, hardware `d3d11va`, libplacebo API 371, and
`sh_dovi_compose_nlq` present with the enhancement layer enabled and absent with
it disabled.

The A/B and determinism figures here were **not** re-measured on the newer
runtime and are not claimed for it. `scripts/New-NativeDvEvidenceSummary.ps1`
refuses to regenerate this summary against a manifest whose commit disagrees with
the runtime that produced the runs, so the two cannot be silently conflated; pass
`-RuntimeManifest` pointing at the manifest those runs used.

## What was run

Three runs, identical in every variable except the enhancement-layer request,
against the unmodified authored 83,875,552,592-byte Profile 7 source at authored
timestamp 300 s:

| Run | Enhancement layer | Purpose |
| --- | --- | --- |
| `proof2-fel-on-a` | `yes` | primary |
| `proof2-fel-on-b` | `yes` | determinism control |
| `proof2-fel-off` | `no` | negative control |

Each used the pinned side-by-side runtime by absolute path with `--no-config`,
`--vo=gpu-next`, `--hwdec=d3d11va`, `--gpu-api=d3d11`, `--gpu-context=d3d11`,
isolated config/cache/watch-later/TEMP locations, and a private named pipe. The
stable `C:\mpv\mpv.exe` was hashed before and after every run and never changed.

## Why the determinism control exists

An enhancement-layer A/B difference only means something if the renderer is
deterministic at the compared timestamp. Without that control, dithering or
ordinary frame-to-frame variation would produce a broad, low-amplitude pixel
difference that looks exactly like a subtle FEL contribution.

`proof2-fel-on-a` and `proof2-fel-on-b` are **bit-identical**: 0 of 921,600
pixels differ, maximum channel delta 0. Both captures also hash to
`1b0b69d0b72ed5c17fe6e6e2436b93c68b3f4dc05826b3d3891e5af29d581711`, which is the
same hash produced by the earlier independent `proof-fel-on-300-d3d11va` run, so
the result reproduces bit-exactly across separate sessions.

Because the renderer is deterministic here, the entire FEL-on/FEL-off difference
is attributable to the enhancement-layer request.

## A. Execution evidence

From `proof2-fel-on-a`, all observed rather than requested:

| Observation | Value |
| --- | --- |
| Profile 7 splitter | BL stream 0, virtual EL stream 1 (`dependent_track`) |
| HEVC decoder instances | 2 |
| Base-layer decoder | `hevc - HEVC (High Efficiency Video Coding)` |
| Enhancement-layer decoder | `hevc - HEVC (High Efficiency Video Coding)` |
| BL/EL pairing | Active — `[vf] [el_pair] 3840x2160 d3d11[p010] dolbyvision/bt.2020/pq/limited/display` |
| RPU | Present |
| libplacebo | v7.371.0, API 371 |
| FEL composition | Active — `sh_dovi_compose_nlq` compiled and an `enhancement layer` shader stage timed every frame |
| Renderer / API / context | gpu-next / d3d11 / d3d11 |
| Hardware surfaces | Active, `hwdec-current=d3d11va`, no software fallback |
| Degradation reported | none |

The FEL-off run is the discriminating control: it produces the **same** splitter,
the **same** two decoder instances, the **same** `el_pair` filter, and the same
hardware decoding — but **zero** `sh_dovi_compose_nlq` occurrences and **zero**
enhancement-layer shader stages. The enhancement layer is still demuxed, decoded
and paired when composition is disabled; only the renderer stage is suppressed.
The A/B therefore isolates composition specifically, not merely EL presence.

### One corrected claim

The earlier evidence reported `DeviceCreationFailureObserved: true` on every run.
That was a false positive. `[vo/gpu-next] Loading failed.` is emitted when
gpu-next declines an optional hwdec interop driver (`d3d11-egl`) after another
driver already succeeded; it appears three times in a completely healthy run.
No device or context creation failure occurred in any run. The benign probe
outcome is now counted separately as `HwdecInteropProbeFailures` rather than
being either discarded or reported as a fault.

## B. Rendered evidence

Both captures were taken at requested timestamp 300.0 s, both settled at observed
300.011 s, well inside the 125 ms protocol tolerance, with identical renderer,
output colour settings and screenshot format.

| Comparison | Changed pixels | Fraction | Max channel delta | Mean absolute error |
| --- | ---: | ---: | ---: | ---: |
| Control (on vs on) | 0 / 921,600 | 0 | 0 | 0 |
| Experiment (on vs off) | 889,197 / 921,600 | 0.9648 | 2,242 / 65,535 | 111.39 / 65,535 |

The changed region spans the full frame. The maximum channel delta is 3.42 % of
the 16-bit sample range and the mean absolute error is 0.17 %, which is the
expected shape of an NLQ residual contribution: broad and low-amplitude rather
than localised. Against a bit-identical control, a non-zero difference of this
size cannot be renderer noise.

Both figures are scored over the three colour channels only; the alpha channel is
excluded from the sum and from the denominator, so an alpha-only change is not
counted as a rendered difference. The changed-pixel count and the maximum channel
delta were reproduced exactly by an independently written PNG decoder, and the
16-bit decode path now has its own regression covering delta truncation and alpha
exclusion.

An independent non-log corroboration: dedicated per-process GPU memory is
approximately 1,253.8 MiB with the enhancement layer enabled and 1,231.6 MiB with
it disabled, a reproducible ~22 MiB difference consistent with enhancement-layer
surfaces resident only when composition runs.

## Zero media scratch

The claim requires two independent measurements to agree, because a before/after
directory snapshot cannot see a media-sized file created and deleted mid-run.

| Run | Media payload bytes | Unknown writes | Bounded state bytes | Process write bytes | Result |
| --- | ---: | ---: | ---: | ---: | --- |
| `proof2-fel-on-a` | 0 | 0 | 6,458,007 | 6,414,924 | Zero |
| `proof2-fel-on-b` | 0 | 0 | 6,449,703 | 6,406,966 | Zero |
| `proof2-fel-off` | 0 | 0 | 5,837,691 | 5,795,100 | Zero |

The process write total is the stronger measurement: it accumulates every byte
the mpv process wrote anywhere, including to files that no longer exist. At
roughly 6 MB it is four orders of magnitude below the 83.88 GB source. No
extracted HEVC, converted HEVC, temporary Matroska, shadow transcode, or growing
media cache was created in any run.

Attributing that total bounds hidden writes tightly. The diagnostic log plus the
single evidence PNG account for 6,339,050 of 6,414,924 bytes on `proof2-fel-on-a`,
6,330,770 of 6,406,966 on `proof2-fel-on-b`, and 5,718,920 of 5,795,100 on
`proof2-fel-off`. At most about 76 KB per run is unattributed, and that residual
is near-identical across all three runs, which is the signature of fixed
filesystem overhead rather than of media content. The gate additionally refuses
any run whose process write total exceeds a 32 MiB ceiling.

A forced-failure run was also exercised: an unreachable capture timestamp made
playback throw with a live mpv process. The run still recorded the error, the
process write counters, the source identity check, the stable-runtime check, and
the zero-scratch verdict, and left no mpv process behind.

Runs that predate the process counters correctly report `Unknown` rather than
`Zero`, because a clean snapshot alone cannot exclude transient writes.

Read traffic was approximately 524 MiB per run — bounded streaming of a 83.88 GB
file, scaling with playback window rather than file size.

## Memory, GPU, CPU, timing

Measured across launch, 30 s sustained playback, pause, resume, and a six-seek
schedule (23 samples per run).

| Metric | FEL on (`proof2-fel-on-a`) | FEL off |
| --- | --- | --- |
| Working set | 401–564 MiB, +21.9 MiB first→last | 381–539 MiB, +21.3 MiB |
| Private bytes | 1,617–1,801 MiB, +14.0 MiB | 1,602–1,780 MiB, +14.2 MiB |
| GPU dedicated (process) | 1,253.5–1,261.4 MiB, +7.6 MiB | 1,231.6–1,239.2 MiB, +7.6 MiB |
| GPU shared (process) | 128.9–279.1 MiB | 128.1–278.0 MiB |
| CPU, sustained | 0.232 % of 32 logical CPUs | 0.241 % |
| VideoDecode engine, sustained mean | 24.95 % | 24.70 % |
| 3D engine, sustained mean | 35.34 % | 19.88 % |
| Startup to first `video-params` | 2,249 ms | 1,626 ms |
| Seek recovery | 58–221 ms, 6/6 within tolerance | 58–161 ms, 6/6 |
| Dropped / decoder-dropped / VO-delayed frames | 0 / 0 / 0 | 0 / 0 / 0 |

The 3D-engine difference between on and off is the composition work itself. The
+7.6 MiB dedicated-VRAM growth appears identically in all three runs including
the one with composition disabled, which identifies it as allocator settling
across seeks rather than an enhancement-layer leak.

Growth is bounded and does not track playback duration or the 83.88 GB file size.
Expected scaling factors remain resolution, decoder surface count, queue depth,
and renderer.

## Source immutability

Length, last-write time, and first/middle/last 16 MiB sentinel SHA-256 values
were verified after launch, sustained playback, pause/resume, seeking, and exit
in every run. All matched. The source was opened read-only throughout.

## Limitations

1. Composition, BL/EL pairing, and the Profile 7 splitter are observed through
   this pinned build's diagnostic log. mpv exposes no structured IPC property for
   them, so those three observations are version-pinned to the manifest runtime.
   Everything else — `hwdec-current`, `gpu-api`, `gpu-context`, the filter chain
   and its parameters, `video-params`, drop counters, `dolby-vision-profile` —
   comes from structured IPC.
2. The deterministic capture is a 1280x720 window screenshot of rendered output,
   not a 3840x2160 frame dump. It proves that enabling the enhancement layer
   changes rendered pixels; it does not quantify the contribution at native
   resolution.
3. The A/B covers one authored timestamp with confirmed non-trivial NLQ metadata.
   It is not a whole-title fidelity measurement.
4. Process write-byte counters bound writes by the mpv process only. Other
   processes are covered solely by the run-directory snapshot.
5. The gate certifies the D3D11VA/D3D11 path. A second path was also exercised:
   `proof-nvdec-vulkan-300` requested `hwdec=nvdec` with `gpu-api=vulkan` and
   `gpu-context=winvk`, and observed `hwdec-current=nvdec`, a Vulkan API, the
   Profile 7 splitter, two decoder instances, `el_pair`, the NLQ shader, and the
   enhancement-layer stage, with no software fallback. That run predates the
   determinism control and the process write counters, so it is reported as a
   second composing configuration rather than as part of this gate.
