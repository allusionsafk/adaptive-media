# Adaptive Media product principles

Adaptive Media is a native Windows media player for users who want dependable playback first and optional enhancement when they choose it.

## Product goals

Opening a file should not require knowledge of mpv, FFmpeg, GPU APIs, or codec internals.

The application should:

- keep the default playback path dependable
- make enhancement choices explicit
- explain when a requested path is unavailable or degraded
- distinguish requested settings from observed runtime state
- keep advanced hardware detail available without making it the main interface
- preserve keyboard access, readable contrast, DPI-aware sizing, and clear text layout

## Playback defaults

Reference playback should preserve the source presentation unless the output device requires conversion or the user enables an enhancement.

Motion interpolation, synthetic HDR, aggressive sharpening, denoising, and reconstruction should not be enabled silently.

## Interface

The Windows application uses familiar native controls, restrained colour, and progressive disclosure.

Media actions and playback choices should be more prominent than implementation details. Technical state belongs in diagnostics and status views where it can explain what the application did.

Avoid:

- console windows as normal product UI
- unsupported format claims
- requested settings presented as active state
- decorative effects that reduce clarity or responsiveness

## Naming

Adaptive Media remains the repository and engineering name. Any future public-facing rename should be handled separately rather than embedded early in APIs, file formats, or internal contracts.
