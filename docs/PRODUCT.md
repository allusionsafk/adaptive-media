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

The Windows application uses familiar native interactions, restrained colour, and progressive disclosure. It is a quiet cinematic launcher around a media-first player: a dark ground that recedes, one primary action, and the technical layer one step away.

Media actions and playback choices should be more prominent than implementation details. Technical state belongs in Playback details and diagnostics, where it can explain what the application did: what was requested, planned and observed, then health and recovery. Healthy playback stays quiet; attention appears only when the player reports a condition that persists or fails.

In the player window, DemiMedia styles mpv's own controls, track lists and a Playback details panel (Tab) from its managed configuration; it does not replace or embed the player.

Visual rules:

- composition over components: fewer boxes, cards and pills; one choice is dominant, the rest stay quiet
- human labels in proportional type, machine-produced values (codecs, decoders, sizes) in mono
- green only for true semantic states such as healthy or verified, never as decoration
- DemiMedia may carry one or two restrained feline signatures as a personality layer noticed late: today the small pair of ears at the start of a selection marker, and the brief tail-like settle when Playback details opens. They must never compete with the media, the controls or playback truth. No cat icons, paw-print buttons, sounds or mascots.

Avoid:

- console windows as normal product UI
- unsupported format claims
- requested settings presented as active state
- decorative effects that reduce clarity or responsiveness

## Naming

Adaptive Media remains the repository and engineering name. Any future public-facing rename should be handled separately rather than embedded early in APIs, file formats, or internal contracts.
