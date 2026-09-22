# Intent-Driven Enhancement Planner V1 Design

## Goal

DemiMedia translates end-result preferences into a deterministic, explainable playback policy before the existing requested → planned → observed → health → recovery pipeline. Users choose source-faithful Reference playback, independent Enhanced dimensions, or goal-oriented Automatic playback without learning renderer and filter flags.

## Architecture

`EnhancementIntent` describes the semantic request. `PlaybackEnvironment` contains only known source, display, renderer, hardware, playlist, and temporal-capability facts. The pure `EnhancementPlanner` returns an immutable `EnhancementDecision` containing selected detail, motion, cleanup, effective legacy playback options, cadence assessment, and branch-derived reasons.

The existing `PlaybackPlanBuilder` remains the only component that emits mpv arguments. It consumes the planner decision and translates selected V1 implementations into the established gpu-next, NVIDIA D3D11 VPP, display-resample, interpolation, and deband arguments. Legacy `PlaybackOptions` callers are adapted into semantic intent so public construction paths remain safe.

## Intent model

- `Reference` is source-faithful: no discretionary detail or cleanup, no temporal blending, and cadence correction only when measured source/display cadence is poor.
- `Enhanced` stores independent `Detail`, `Motion`, `Cleanup`, and `Performance` preferences.
- `Automatic` stores `Goal`, `Strength`, and `Performance`; the planner derives the four implementation dimensions from those result-oriented choices.
- Detail supports Preserve, Balanced, Sharper, Maximum, and Automatic.
- Motion distinguishes Original, CadenceCorrected, BlendSmooth, GeneratedMotion, NeuralMotion, and Automatic. V1 executes only the first three.
- Cleanup supports PreserveTexture, Balanced, Clean, and Automatic.
- Performance supports Efficient, Balanced, and MaximumQuality and materially changes optional detail/cleanup selection.

## Deterministic policy

Detail selection first proves that an upscale is needed. NVIDIA VPP is eligible only when dimensions and aspect fit are known, scale is within the supported range, NVIDIA RTX and d3d11vpp are available, and the playback path is not Compatibility. MaximumQuality may select NVIDIA VPP for a suitable maximum-detail request; Balanced selects conventional high-quality scaling; Efficient rejects marginal optional work. Ineligible NVIDIA requests fall back to conventional scaling with an explicit reason.

Cadence is clean only when both rates are known and the display/source ratio is close to an integer. A clean cadence never implies high temporal resolution: smoother-motion goals may still select BlendSmooth. Preserve-cinematic requests use CadenceCorrected only for measured poor cadence. Missing refresh information stays Unknown and produces a conservative decision. Requests for GeneratedMotion or NeuralMotion fall back to BlendSmooth with a truthful reason because those backends are unavailable in V1.

Cleanup preserves texture by default. Balanced uses the established moderate cleanup path; Clean uses the stronger supported path. Automatic cleanup is conservative and only selects gentle cleanup for a single known 8-bit SDR source. It does not invent grain, compression, or banding analysis.

## Truth and compatibility

`PlaybackPlan` carries the semantic intent and immutable decision in addition to the legacy requested options. Playback Truth gains an Intent section before Requested. Requested names the selected implementation tier; Planned is still derived from the actual argument vector; Observed remains derived only from current-attempt evidence. Planner requests never populate Observed.

New settings fields use existing JSON reflection, canonical validation, extension-data preservation, and atomic save behavior. A schema-1 file without the new fields maps legacy Enhanced choices to equivalent explicit preferences; Automatic and Reference receive safe semantic defaults. No broad migration rewrites user files merely because fields are absent.

## UI

The existing Playback choices card uses progressive disclosure. Reference stays simple. Enhanced shows Detail, Motion, Cleanup, and Performance. Automatic shows What matters most, Strength, and Performance. Compatibility remains the established fallback path. The normal surface uses result language; implementation names stay in the plan/truth explanation.

## Verification

Pure planner tests cover all required detail, reference/enhanced, motion, cleanup, truth, compatibility, missing-data, determinism, and settings cases. Focused mutation probes flip upscale need, NVIDIA eligibility, cadence quality, future motion capability, performance budget, and observed evidence independently. One bounded real-product-path test builds the ordinary 1080p → 1440p eligible plan without launching mpv and proves intent → request → plan while Observed remains empty until evidence exists.
