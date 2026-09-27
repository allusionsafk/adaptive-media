# Generated Motion stop reliability

Date: 2026-09-27. RTX 4080 Laptop GPU, driver 617.14. Runtime gm-2026-09-26-1 (as shipped in c9a866f) and its successor gm-2026-09-27-1.

## Symptom

About 1 in 12 Desktop Generated Motion sessions ended with the player exiting 0xC0000005 after playback had already stopped. DemiMedia then reported "Playback failed". A crash after end of file could also be misread as a runtime failure and trigger a stock-player recovery after the film had finished.

## Reproduction

A stress harness launched the runtime with DemiMedia's exact argument vector and rotated five stop modes: IPC quit, early quit, end of file, seek then quit, and window close. An env-gated lifecycle trace (`DEMIMEDIA_NVOFRUC_TRACE`) flushed each filter stage to a file.

| Configuration | Runs | Faulted exits |
| --- | ---: | ---: |
| Shipped filter | 25 | 6 (24%) |
| FRUC explicitly unregistered and destroyed before teardown | 25 | 3 (one 0xC0000409) |
| No filter (control) | 25 | 0 |
| Filter in passthrough, FRUC never loaded (control) | 25 | 0 |
| **d3d11.dll pinned after FRUC creation** | **50** | **0** |

In every faulted run, the trace ended at filter `uninit-end`, before the process-detach marker.

## Ownership

Windows Application Error events (ID 1000) for every 0xC0000005 exit, including the original Desktop crash on the shipped runtime, name the faulting module `d3d11.dll_unloaded` (10.0.26100.9278) at offset 0x62095. The one 0xC0000409 exit faulted in ntdll.dll at offset 0x13dca5.

Code inside d3d11.dll ran after d3d11.dll had been unloaded during the renderer's shutdown. It happens only after FRUC's CUDA interop and the D3D11 video processor have used the device, and it survives FRUC's own destroy. That places it in a late vendor thread (NVIDIA, CUDA or D3D11), not in the filter, NvOFFRUC.dll's API use, FFmpeg or mpv. No crash dump was captured, because that needs a machine-wide Windows Error Reporting registry change, so the exact thread is not identified.

## Changes

1. **Filter:** after FRUC is created, `d3d11.dll` is pinned for the life of the runtime process, so a late vendor thread can never execute unmapped code. This is the narrow fix; the stress above falsifies the old behaviour.
2. **Filter:** a `teardown` filter command stops new FRUC work, waits for the last fence, unregisters resources and destroys FRUC. It completed in all 25 runs in which it was issued.
3. **DemiMedia, requested stop:** sends `teardown` and then `quit`; if the runtime has not exited after 3 s, that exact process is terminated.
4. **DemiMedia, containment:** on the Generated Motion renderer only, a non-zero exit after a requested stop, or a Windows fault exit after the player reported end of file, is recorded as a completed stop with a diagnostics note. A dedicated event listener makes the end-of-file reason reliable. A fault during active playback is still a runtime failure and still resumes on the stock player.
5. The lifecycle trace stays compiled in, but it is off unless `DEMIMEDIA_NVOFRUC_TRACE` is set.
