# Adaptive Media product principles

Adaptive Media is a native Windows media player for people who want dependable playback first and optional enhancement when they ask for it.

## Product contract

Opening a file should not require the user to understand mpv, FFmpeg, GPU APIs, or codec plumbing.

The application should:

- make the default playback path dependable
- keep enhancement choices explicit
- explain when the requested path is unavailable or degraded
- distinguish requested settings from observed runtime state
- keep advanced hardware detail available without making it the main interface
- preserve keyboard access, readable contrast, DPI-aware sizing, and clear wrapped copy

## Reference first

Reference playback should preserve source presentation unless a conversion is required by the output device or the user deliberately enables an enhancement.

Do not silently enable motion interpolation, synthetic HDR, aggressive sharpening, denoising, or reconstruction.

## Interface direction

The current Windows application uses familiar native controls, restrained colour, and progressive disclosure.

Media actions and playback choices should remain more prominent than implementation detail. Technical state belongs in diagnostics and evidence surfaces where it can explain what the application actually did.

Avoid:

- console windows as normal product UI
- fake technical atmosphere
- unsupported format claims
- requested settings presented as active state
- visual effects that make the interface slower without improving comprehension

## Naming

Adaptive Media remains the repository and engineering name. Any future public-facing rename is a separate decision and should not be baked into shared APIs, file formats, or internal contracts prematurely.
