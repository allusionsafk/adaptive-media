# Native Dolby Vision Experimental Integration Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Gate:** `docs/native-dv-p7/direct-playback-summary.json` reports `supported`, so this plan is authorised. Re-read that file before starting; if a later experiment pass changes the gate, stop and revise this plan instead of implementing it.

**Goal:** Make the proven native Profile 7 lane reachable from the application, without altering stable playback, stable runtime provisioning, or Compatibility Export.

**Spec:** `docs/superpowers/specs/2026-09-10-native-zero-disk-dv-p7-design.md`
**Evidence:** `docs/native-dv-p7/DIRECT-PLAYBACK-EVIDENCE.md`

## What already exists

Delivered on `feature/native-zero-disk-dv-p7` and covered by the Dolby Vision
semantic suite (58 → 99 assertions):

- `src/AdaptiveMedia.App/NativeDvPlayback.cs` — `NativeDvRuntime`,
  `NativeDvRequest`, `NativeDvObservation`, `NativeDvPlaybackPlan`,
  `NativeDvPlaybackPlanner`, `NativeDvObservationReducer`.
- Requested and observed state are separate types. `Delivered` is derived from
  observed composition only; a mutation deriving it from the request fails the
  suite.
- Tri-state `DvObservedState` with Unknown never promoted.
- `RequiresConversion` structurally false, `MediaScratchPaths` empty,
  `--cache-on-disk=no` as a lane invariant, audio and subtitles preserved.
- Opt-in rejection paths for unclassified MEL/FEL, unvalidated RPU, wrong
  profile, non-HDR10 base, relative runtime, stable `C:\mpv` runtime, and a
  libplacebo API below the composing minimum.

## The blocker this plan must clear first

`PlaybackService.ResolveMpv` (`src/AdaptiveMedia.App/PlaybackService.cs:166`)
resolves only the stable runtime: `tools/mpv.exe`, `C:\mpv\mpv.exe`, then PATH.
`NativeDvPlaybackPlanner` deliberately refuses every one of those, because the
native lane requires the pinned post-FEL build.

The pinned runtime currently lives under `.artifacts/native-dv/runtime/`, which
is git-ignored and is not shipped by the installer. **There is therefore no way
for the application to obtain a runtime the native lane will accept.** Closing
that gap is Task 1 and is the reason this work is a separate milestone: the
previous milestone's scope explicitly forbade changing dependency provisioning.

Do not solve this by relaxing the planner's runtime checks.

---

### Task 1: Provision the experimental runtime separately from the stable one

**Files:**
- Create: `src/AdaptiveMedia.App/NativeDvRuntimeStore.cs`
- Modify: `tests/DolbyVisionTests/Program.cs`, `tests/DolbyVisionTests/DolbyVisionTests.csproj`

**Interfaces:**
- Consumes: a manifest with the same shape as `docs/native-dv-p7/runtime-manifest.json`.
- Produces: `NativeDvRuntime?` — non-null only when an extracted runtime is present and its SHA-256 matches the manifest.

- [ ] **Step 1: Write failing tests** covering: a missing runtime returns null; a
  hash mismatch returns null and never returns a runtime; a manifest naming a
  path under `C:\mpv\` is refused; a relative path is refused; a valid pinned
  tree returns a runtime whose `ExecutablePath` is the console launcher.

- [ ] **Step 2: Implement the store.** Resolve strictly from the manifest,
  verify length and SHA-256 before returning, and never fall back to the stable
  runtime or PATH. Leave `ResolveMpv` untouched.

- [ ] **Step 3: Decide acquisition and record it.** Either the user points the
  application at an existing extracted runtime, or a separate opt-in download is
  added. Do not extend the stable provisioner. Whichever is chosen, the manifest
  hash check stays mandatory.

### Task 2: Reduce real IPC into the observation model

**Files:**
- Modify: `src/AdaptiveMedia.App/PlaybackService.cs`, `src/AdaptiveMedia.App/NativeDvPlayback.cs`
- Modify: `tests/DolbyVisionTests/Program.cs`

**Interfaces:**
- Consumes: the properties `PlaybackService` already collects at
  `src/AdaptiveMedia.App/PlaybackService.cs:131` — `gpu-api`, `gpu-context`,
  `hwdec-current`, `vf`, `video-params` — plus the pinned runtime's log.
- Produces: a populated `NativeDvObservation` per session.

- [ ] **Step 1: Write failing tests** that build an observation from captured
  IPC JSON fixtures, including a fixture with properties missing entirely.
  Missing properties must yield Unknown, never Inactive.

- [ ] **Step 2: Implement the structured reduction.** `hwdec-current`,
  `gpu-api`, `gpu-context` and `video-params.pixelformat` come from IPC.

- [ ] **Step 3: Implement the narrowly versioned diagnostic adapter.**
  Composition, BL/EL pairing, and the Profile 7 splitter have no structured
  property in this runtime. Parse them only from the pinned build's log, gate the
  adapter on the manifest's mpv commit, and return Unknown when the commit does
  not match rather than applying the patterns to an unknown build. Reuse the
  patterns proven in `scripts/NativeDvExperiment.psm1`, including the exclusion
  that stops `[vo/gpu-next] Loading failed.` reading as a device failure.

- [ ] **Step 4: Consider the upstream property.** If maintaining the adapter
  proves fragile, prepare a minimal upstream patch exposing composition state as
  an mpv property, and propose it. Do not fork mpv.

### Task 3: Select the lane and surface truthful state

**Files:**
- Modify: `src/AdaptiveMedia.App/PlaybackService.cs`, `src/AdaptiveMedia.App/SessionDiagnostics.cs`, `src/AdaptiveMedia.App/Models.cs`
- Modify: `src/AdaptiveMedia.App/SettingsWindow.xaml.cs` and `.xaml`
- Modify: `tests/SettingsTests/Program.cs`, `tests/AdaptiveMedia.Tests/Program.cs`

- [ ] **Step 1: Write failing tests** requiring that the lane is never selected
  unless explicitly enabled; that a rejected native plan falls back to stable
  playback with the rejection recorded; that diagnostics serialise requested and
  observed separately; and that no diagnostics field can present a requested
  value as an observed one.

- [ ] **Step 2: Add the explicit opt-in.** A single developer-facing setting,
  defaulting off, round-tripped through `AppSettings` with its existing
  unknown-key preservation. No broad UI redesign.

- [ ] **Step 3: Wire selection.** When enabled and the store returns a runtime,
  build the native plan; on any rejection, record the `NativeDvRejection` and use
  the existing stable path. Never call `DvConversionPlanner` or
  `DvMatroskaP81Executor` from this path.

- [ ] **Step 4: Surface the result.** Show `NativeDvObservation.Summary`. A
  base-layer-only or unknown result must never be presented as Full FEL. Keep
  `DiagnosticsStore.Redact` applied to the source path.

### Task 4: Cancellation, cleanup, and stable-runtime isolation

- [ ] **Step 1: Write failing tests** for cancellation during native playback,
  for cleanup removing only bounded diagnostic state, and for the stable runtime
  hash being unchanged across a native session.

- [ ] **Step 2: Implement** cancellation and cleanup that never delete media and
  never touch `C:\mpv`, PATH, or file associations.

### Task 5: Real hardware proof through the application

- [ ] **Step 1:** Play the authored source through Adaptive Media with the lane
  enabled and capture the same evidence the harness captures: pipeline state,
  process write bytes, per-process GPU memory, drops, seek recovery.

- [ ] **Step 2:** Confirm zero media-sized scratch using both measurements, and
  confirm audio, subtitles, chapters, and attachments are all present — the
  harness disables audio and subtitles, so this is the first time the full
  container is exercised.

- [ ] **Step 3:** Compare application-observed state against harness-observed
  state for the same timestamp. They must agree; any disagreement is a defect in
  the adapter, not a tolerance to widen.

### Task 6: Full validation and handoff

- [ ] Run the complete gate: all assertion suites, application build at zero
  warnings, packaging, reconstruction, layout, PowerShell syntax, installer
  compile, and `git diff --check`.
- [ ] Assess installer impact of shipping or not shipping the experimental
  runtime, and record the decision.
- [ ] Do not merge, tag, release, or publish.

## Explicit exclusions

Unchanged from the parent spec: no Profile 7 to Profile 8.1 conversion, no
change to Compatibility Export semantics, no media-sized scratch, no
modification of the authored source, no change to `C:\mpv`, PATH, or file
associations, no Profile 5, no NVENC export, no AI enhancement, no Shadow
Stream, no installer redesign, no release.
