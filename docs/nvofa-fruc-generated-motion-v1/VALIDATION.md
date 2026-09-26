# Generated Motion (NVIDIA Optical Flow FRUC) validation

Date: 2026-09-26. RTX 4080 Laptop GPU, driver 617.14, AC power, Energy Saver off, 2560×1600 240 Hz internal panel. SDK: Optical Flow SDK 5.0.7 (downloaded by the user under NVIDIA's license; not in this repository). Background GPU load from other applications was present during the lab benchmarks, so those timings are conservative.

## Official sample

The SDK's NvOFFRUCSample built with MSVC 14.44 and CMake, without a CUDA toolkit, and ran on the RTX 4080. Output format findings on this SDK and driver:

| Surface / allocation | Interpolated chroma |
| --- | --- |
| NV12, D3D11 | 99.2% zero (green frames); source frames correct |
| NV12, CUDA | non-zero but corrupt (magenta cast, blocky banding) |
| ARGB, D3D11 | correct |

Generated Motion therefore uses ARGB, which is 8-bit: it is limited to 8-bit SDR sources. HDR, WCG and Dolby Vision keep their own paths.

## Throughput (lab harness, D3D11 ARGB, GPU-resident, 480 input frames)

Synthesized-frame latency per FRUC call, submit to fence completion:

| Case | Synthesized / requested | Mean | p95 | Real-time budget per synthesized frame |
| --- | ---: | ---: | ---: | --- |
| 1080p 24→48 | 460 / 479 | 13.2 ms | 25.0 ms | 41.7 ms: yes |
| 1080p 24→60 | 920 / 958 | 12.1 ms | 24.5 ms | 27.8 ms: yes, p95 tight |
| 1440p 24→48 | 450 / 479 | 18.7 ms | 36.9 ms | 41.7 ms: marginal (p99 42.9 ms) |
| 2160p 24→48 | 240 / 479 | 56.5 ms | 90.5 ms | 41.7 ms: no (0.61× real time) |

FRUC VRAM was roughly 370 MB at 1080p, 500 MB at 1440p and 940 MB at 4K. Instance creation took 115–215 ms.

## Behaviour

- The first output after every hard cut is flagged as a repeated frame; no cross-cut blending was observed.
- FRUC refuses synthetic test patterns with flicker or colour cycling (0% synthesized) and synthesizes smooth natural-like motion (94–96%). The repetition flag is a truthful generated-versus-repeated signal.
- Non-midpoint timestamps (24→60) are accepted and synthesized.
- After NvOFFRUCDestroy, a new instance in the same process cannot register resources (INVALID_HANDLE). A fresh process always recovers. The filter keeps one instance per player process, and a failed player is replaced by the stock player.

## Runtime

An app-private mpv runtime (MSYS2 mpv 0.41.0-8, libplacebo 7.360.1) with FFmpeg 9.0.2 built by `runtimes/fruc-mpv/build.sh`, adding the `nvofruc` filter. Decoded D3D11 NV12 frames stay on the GPU: the D3D11 video processor converts to RGBA, FRUC synthesizes, and the video processor converts the new frame back to NV12. Source frames are copied unchanged. The stock DemiMedia player is not modified and remains the fallback.

## DemiMedia validation (real launch path, isolated data directory)

| Check | Result |
| --- | --- |
| 20 s 1080p24 smooth clip, Enhanced + Generated Motion | Verified: 466 synthesized, 2 repeated at the two cuts, 48 fps filter output, display-resample active, jitter 0.002, 0 drops |
| 178 s sustained clip | Verified: 4,200 synthesized, 71 repeated, 0 output or decoder drops, 3 delayed, Healthy throughout; 33% GPU, ~700 MB VRAM above idle, 28 W |
| Seek forward and back (mpv level) | FRUC continued across graph rebuilds; 0 errors, 0 drops; max A/V sync offset 15 ms |
| Simulated backend failure | Filter switched to passthrough; playback continued with blend smoothing; the fallback stays visible after a seek |
| Filter that cannot start | mpv disabled it and kept playing at the source rate |
| Runtime process killed mid-film | DemiMedia resumed once on the stock player with blend smoothing, at the confirmed position minus the standard 3 s rewind |
| Smart Fill together with Generated Motion | Smart Fill verified; Generated Motion correctly unverified on a synthetic pattern FRUC refuses |

A first real run showed mpv abandoning display-resample (vsync jitter 0.15–0.29, 21–30 delayed frames). Isolation runs showed the runtime alone was clean and blending was not the cause. A six-deep swapchain on this lane removed the delayed frames (0–3 per run) and kept display-resample active.

## Not established

- Real GPU device loss or TDR during Generated Motion was not induced; the recovery path was exercised by killing the runtime process.
- Authored film content was not evaluated; all media were generated.
- Perceptual quality is judged from spot checks of generated frames only.
- Automatic never selects Generated Motion.
