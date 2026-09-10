# Authored Profile 7 source classification

Status: **confirmed Profile 7 FEL with active, non-trivial NLQ contribution**.

Evidence run: `.artifacts/native-dv/runs/classification-300-d3d11va/`.

## Source identity

The committed record intentionally redacts the title-bearing filename. The ignored run manifest contains the exact absolute path and unchanged argument value.

| Fact | Value |
| --- | --- |
| Length | 83,875,552,592 bytes |
| Last write | `2026-09-10T02:57:29.4691122Z` |
| Sentinel size | 16,777,216 bytes each |
| First offset / SHA-256 | `0` / `bb2efe1867a19ebbeda93dee9f98d122bacd790b000f8a0693c748a152ccd4ab` |
| Middle offset / SHA-256 | `41,929,387,688` / `f5ca79f2726efd5af5db915da9cb9c9ac1858dd28d1f69ee1e689b16e9f71450` |
| Last offset / SHA-256 | `83,858,775,376` / `f9a3b1900dd8b02e2e8a2f53f925a628bb76ba90fe7d835ad347dfdb867d99cd` |

The identity was rechecked after classification, launch, sustained playback, pause/resume, repeated seeking, and exit. All checks matched. The C: volume had 58,090,516,480 free bytes at evidence review; no full-file conversion or extraction was attempted.

## Independent classification evidence

ffprobe reported one 3840x2160 HEVC Main 10 video track with `yuv420p10le`, limited range, BT.2020 non-constant-luminance matrix/primaries, and SMPTE ST 2084 transfer. Its Dolby Vision configuration record is version 1.0, profile 7, level 6, with BL, EL, and RPU all present and compatibility ID 6. The Matroska duration is 7,936.064 seconds and its reported size agrees with the filesystem length.

FFmpeg stream-copied video starting at authored time 300 seconds directly through an anonymous pipe. No HEVC payload was written. Pinned dovi_tool 2.3.3 stopped after the bounded sample and wrote a 26,014-byte RPU-only file. Its summary reported:

```text
Frames: 121
Profile: 7 (FEL)
DM version: 1 (CM v2.9)
Scene/shot count: 1
```

The first sampled RPU independently contains:

- `el_type: FEL` and `dovi_profile: 7`;
- `el_spatial_resampling_filter_flag: true`;
- `disable_residual_flag: false`;
- `nlq_method_idc: LinearDeadzone`;
- NLQ offsets `[512, 512, 512]`; and
- linear-deadzone slopes `[2048, 2048, 2048]`.

This is explicit residual/NLQ picture metadata, not an inference from `el_present_flag`.

## Independent runtime evidence at 300 seconds

The side-by-side mpv run used D3D11VA/D3D11 and enhancement-layer enabled. It reported the Profile 7 splitter, two HEVC decoder openings, `el_pair`, libplacebo API 371, a compiled `sh_dovi_compose_nlq` shader, and measured GPU time in the enhancement-layer shader stage. Structured IPC reported `hwdec-current=d3d11va`; the log recorded hardware decode and no software fallback. The 300-second capture settled at 300.011 seconds, inside the 125 ms protocol tolerance.

The metadata classification and renderer observation are independent proofs that this source is FEL and that the selected timestamp exercises NLQ. The later FEL-on/FEL-off comparison remains required before product support can be declared.

## Storage and mutation result

The measured post-baseline delta was 4,741,686 bytes of bounded evidence, zero media payload bytes, and zero unknown bytes. Material bounded paths were:

| Producer | Relative path | Bytes | Class |
| --- | --- | ---: | --- |
| dovi_tool | `state/classification.rpu.bin` | 26,014 | RPU metadata only |
| ffprobe/harness | `classification-ffprobe.json` | 33,739 | metadata |
| mpv | `logs/mpv.log` | 973,011 | diagnostic log |
| mpv | `captures/enhancement-yes.png` | 3,549,612 | one lossless evidence frame |
| harness | all remaining changed evidence files | 159,310 | JSON/JSONL/CSV state |

No extracted video, converted video, temporary media container, progressively growing media-sized cache, or shadow media file was created. The stable `C:\mpv\mpv.exe` length, timestamp, and SHA-256 matched before and after.
