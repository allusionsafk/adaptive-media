# Intent-Driven Enhancement Planner V1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a pure semantic enhancement planner, integrate it with playback truth/settings, and expose minimal goal-oriented WPF controls.

**Architecture:** A new pure planner converts semantic intent plus known environment facts into an immutable decision. Existing playback-plan construction translates that decision into mpv arguments, while truth, settings, diagnostics, and the main-window controls carry semantic data without weakening requested/planned/observed separation.

**Tech Stack:** C# 14 / .NET 10, WPF, console-style deterministic test projects, PowerShell repository gates.

**Spec:** `docs/superpowers/specs/2026-09-22-intent-enhancement-planner-v1-design.md`

## Global Constraints

- Work only on `codex/intent-enhancement-planner-v1` from `b12737fd9b61fc66d3183e1609f2dcca94700ef0`.
- Do not change playback-health thresholds, recovery budgets, native runtime lifecycle, compatibility export, or external runtimes.
- Do not implement or claim generated/neural motion; mpv interpolation is temporal blend smoothing.
- Preserve compatibility-sensitive AdaptiveMedia internal names and old settings.
- Observed truth must come only from runtime evidence.

## Review Focus

- Missing/zero source or display facts must select conservative policy and never fabricate cadence precision.
- A hardware identity alone must never prove NVIDIA VPP eligibility or execution.
- Clean cadence must not suppress explicitly requested smoothing.
- Legacy construction and schema-1 settings must retain safe behavior without silently becoming aggressive.
- Reference and Compatibility must not inherit Enhanced implementation choices.

---

### Task 1: Pure semantic planner

**Files:**
- Create: `src/AdaptiveMedia.App/EnhancementPlanning.cs`
- Create: `tests/AdaptiveMedia.Tests/EnhancementPlannerTests.cs`
- Modify: `src/AdaptiveMedia.App/Models.cs`
- Modify: `tests/AdaptiveMedia.Tests/AdaptiveMedia.Tests.csproj`
- Modify: `tests/AdaptiveMedia.Tests/Program.cs`

**Interfaces:**
- Consumes: `MediaInfo`, `PlaybackTarget`, `PlaybackCapabilities`.
- Produces: `EnhancementIntent`, `PlaybackEnvironment`, `EnhancementDecision`, `EnhancementPlanner.Decide`, and `EnhancementPreferences` legacy/settings adapters.

- [ ] **Step 1: Write failing planner tests**

Add literal assertions for no-upscale, eligible/ineligible NVIDIA, Efficient versus MaximumQuality, Reference, independent Enhanced dimensions, clean/poor/unknown cadence, clean-cadence smoothing, unsupported generated/neural fallback, cleanup texture preservation/clean strength, and deterministic equality.

- [ ] **Step 2: Verify RED**

Run: `dotnet run --project tests/AdaptiveMedia.Tests/AdaptiveMedia.Tests.csproj -c Release`

Expected: compilation fails because the semantic planner types do not exist.

- [ ] **Step 3: Implement the minimal pure planner**

Use enums for semantic choices and implementation tiers, immutable records for inputs/outputs, branch-local reason strings, integer-ratio cadence assessment with explicit Unknown, and adapters from legacy playback options/settings.

- [ ] **Step 4: Verify GREEN**

Run the same command. Expected: all planner and existing assertions pass.

### Task 2: Playback-plan and truth integration

**Files:**
- Modify: `src/AdaptiveMedia.App/PlaybackPlan.cs`
- Modify: `src/AdaptiveMedia.App/PlaybackTruth.cs`
- Modify: `src/AdaptiveMedia.App/PlaybackService.cs`
- Modify: `src/AdaptiveMedia.App/SessionDiagnostics.cs`
- Modify: `tests/AdaptiveMedia.Tests/Program.cs`
- Modify: `tests/AdaptiveMedia.Tests/PlaybackTruthTests.cs`

**Interfaces:**
- Consumes: `EnhancementDecision.EffectiveOptions`, selected implementation tiers, and deterministic reasons.
- Produces: exact mpv argument vectors plus `PlaybackPlan.Intent` and `PlaybackPlan.Decision`; truth order becomes Intent → Requested → Planned → Observed → Health → Recovery → Why.

- [ ] **Step 1: Write failing integration/truth tests**

Assert CadenceCorrected emits display-resample without interpolation, BlendSmooth emits existing interpolation/tscale, NVIDIA decision emits D3D11VA → d3d11vpp → gpu-next, conventional fallback emits libplacebo scaling, Requested never enters Observed, and Generated/Neural wording never appears as executed.

- [ ] **Step 2: Verify RED**

Run the AdaptiveMedia test project. Expected: new plan/truth members and behavior are absent.

- [ ] **Step 3: Integrate planner and argument translation**

Plan once from semantic inputs, translate selected implementations into legacy options/arguments, attach intent/decision to native and stable plans, retain reasons through fallback, add semantic fields to redacted diagnostics, and derive truth sections from plan data/evidence at the correct stage.

- [ ] **Step 4: Verify GREEN**

Run the AdaptiveMedia test project. Expected: all assertions pass.

### Task 3: Settings compatibility

**Files:**
- Modify: `src/AdaptiveMedia.App/Models.cs`
- Modify: `src/AdaptiveMedia.App/SettingsStore.cs`
- Modify: `tests/SettingsTests/SettingsTests.csproj`
- Modify: `tests/SettingsTests/Program.cs`

**Interfaces:**
- Consumes: legacy `DefaultUpscaleMode`, `DefaultMotionMode`, `DefaultCleanupMode`, and profile fields.
- Produces: canonical Automatic goal/strength, Enhanced detail/motion/cleanup, and performance strings while preserving extension data.

- [ ] **Step 1: Write failing settings tests**

Load a legacy Enhanced JSON file with no semantic fields and assert equivalent explicit preferences; load missing/invalid semantic fields and assert safe independent repair; save/reload and assert unknown fields remain.

- [ ] **Step 2: Verify RED**

Run: `dotnet run --project tests/SettingsTests/SettingsTests.csproj -c Release`

Expected: new settings properties or mappings are absent.

- [ ] **Step 3: Implement bounded compatibility mapping**

Add string-backed persisted properties, canonical validation, and root-property presence checks so only genuinely absent new fields derive from old choices.

- [ ] **Step 4: Verify GREEN**

Run the Settings and AdaptiveMedia test projects. Expected: both pass.

### Task 4: Minimal WPF intent surface

**Files:**
- Modify: `src/AdaptiveMedia.App/MainWindow.xaml`
- Modify: `src/AdaptiveMedia.App/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: persisted semantic choices and `EnhancementPreferences.IntentFor`.
- Produces: profile-dependent Automatic/Enhanced controls and `PlaybackOptions` carrying `EnhancementIntent`.

- [ ] **Step 1: Add semantic UI-state assertions to the pure adapter tests**

Assert Automatic UI values create goal-oriented intent, Enhanced values remain independent, and Reference ignores stale aggressive saved values.

- [ ] **Step 2: Verify RED**

Run the AdaptiveMedia test project. Expected: adapter entry points are absent.

- [ ] **Step 3: Implement progressive disclosure**

Replace technical upscaling/motion/cleanup controls with an Automatic panel (`What matters most`, `Strength`, `Performance`) and an Enhanced panel (`Detail`, `Motion`, `Cleanup`, `Performance`). Keep Reference simple, Compatibility available, remembered defaults persisted, keyboard labels and AutomationProperties names intact, and implementation terminology out of the normal controls.

- [ ] **Step 4: Build and mechanically inspect**

Run: `dotnet build src/AdaptiveMedia.App/AdaptiveMedia.App.csproj -c Release`

Expected: build succeeds with no warnings or errors. Run Impeccable detection once against the changed XAML/C# targets and fix only concrete findings in one bounded batch.

### Task 5: Falsification, product-path validation, and repository gate

**Files:**
- Modify only files required by a reproduced defect.

**Interfaces:**
- Consumes: complete implementation and required policy invariants.
- Produces: evidence for mutation resistance, regression safety, exact diff scope, commit, and push.

- [ ] **Step 1: Run focused mutation probes**

Exercise hardware-without-upscale, VPP-without-RTX, clean-cadence-with-smoothing, unknown-refresh, unavailable future motion, settings field omission, and empty Observed evidence. Expected: every mutation chooses the documented lower tier and reason.

- [ ] **Step 2: Run bounded real-product-path validation**

Build a 1920×1080 source / 2560×1440 target with eligible NVIDIA facts through the real plan builder without launching mpv. Expected: semantic maximum-detail intent selects NVIDIA VPP arguments, while truth Observed reports no runtime claim before evidence.

- [ ] **Step 3: Run affected regressions and one comprehensive gate**

Run targeted truth/health/recovery/native tests, then exactly one `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/Invoke-Validation.ps1`. Expected: every gate exits 0.

- [ ] **Step 4: Review and finish**

Inspect `git diff --check`, changed files, scope guards, settings compatibility, and requested/planned/observed trust boundaries. Run the worktree identity gate, commit coherently, and push `codex/intent-enhancement-planner-v1` without merging or releasing.
