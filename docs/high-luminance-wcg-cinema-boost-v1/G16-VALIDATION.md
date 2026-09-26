# G16 WCG and Cinema Boost validation

Date: 2026-09-25. Target: BOE0C4B / NE160QDM-NZ8, 2560×1600 at 240 Hz on the active RTX 4080 route. Windows reported HDR unsupported/inactive, WCG supported, a 10-bit output descriptor, P3-like primaries, and 270 cd/m² maximum and full-frame luminance descriptors. These descriptors are software reports, not a photometer measurement. Windows WCG was off before and after each lab session.

## Renderer comparison

The same generated PQ/BT.2020 HEVC clip was played through gpu-next/D3D11/D3D11VA with an FP16 linear D3D11 target. Each run explicitly set both `--target-peak` and `--hdr-reference-white` to the requested value, `--target-prim=display-p3`, `--target-trc=bt.1886`, and `--tone-mapping=mobius`. Windows WCG was temporarily enabled on the exact active target with an independent restoration watchdog. The panel remained at 80% brightness.

| Requested peak and reference white | Sample time | Reported renderer max luma | Renderer transfer / format | Hardware decode | Dropped / delayed frames |
| ---: | ---: | ---: | --- | --- | ---: |
| 270 nits | 20 s | 270 nits | scRGB / rgba16hf | D3D11VA | 14 / 0 |
| 350 nits | 10 s | 350 nits | scRGB / rgba16hf | D3D11VA | 6 / 0 |
| 400 nits | 10 s | 400 nits | scRGB / rgba16hf | D3D11VA | 9 / 0 |
| 450 nits | 10 s | 450 nits | scRGB / rgba16hf | D3D11VA | 8 / 0 |
| 500 nits | 10 s | 500 nits | scRGB / rgba16hf | D3D11VA | 7 / 0 |

Decoder drops were zero in all five runs. The longer runs used concurrent NVIDIA telemetry sampling and are performance observations, not a clean frame pacing certification. Short 350–500 nit Mobius and spline checks both reached their requested scRGB max-luma target without drops, but neither establishes physical luminance or perceptual superiority. Automatic uses the bounded OS descriptor, 270 nits, with Mobius only when Windows WCG is already active on the exact matched display. If WCG is off, the ordinary HDR-to-SDR mapping remains selected. 350–500 are laboratory requests only.

The temporary WCG transaction returned to SDR/WCG-off. Brightness was 80% both before and after. A generated Profile 7 FEL structural fixture, played with the pinned native runtime under temporary WCG, exposed a Profile 7 splitter, two HEVC decoder openings, EL pairing, and the NLQ composition shader; observed input was PQ/BT.2020 and output was scRGB/rgba16hf at the 270-nit renderer target with D3D11VA. This is a structural fixture, not authored Dolby mastering evidence. The previously validated full-length authored movie was absent at its recorded path in this pass.

## Output vocabulary

WCG is SDR display signalling. `video-target-params.max-luma` is a renderer parameter, not measured panel light output. A 100% Windows brightness setting is also not a nit measurement. Dolby Vision metadata and FEL processing do not establish proprietary Dolby Vision display signalling. Playback Truth therefore uses **High-luminance wide-gamut SDR** only after the matched active Windows WCG state and scRGB/FP16 renderer parameters are observed.

## Cinema Boost ownership probe

With AC present and Energy Saver off, the exact active WMI brightness panel was bound to the DisplayConfig target through the full 256-byte EDID hash; the two Windows PnP instance paths differed. The independent helper read 80%, set 100%, and observed 100% on readback. Closing the session restored 80%. In a second bounded run, an external WMI change set brightness to 90% after the helper reached 100%; the helper left 90% intact. Lab cleanup then restored the initial 80%, and Windows WCG returned to off/SDR. Brightness percentages remain separate from renderer target nits.
