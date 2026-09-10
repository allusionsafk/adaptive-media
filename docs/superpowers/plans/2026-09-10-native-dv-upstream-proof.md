# Native Dolby Vision Upstream and Runtime Proof Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce a reproducible, isolated, zero-media-scratch proof of whether a pinned post-FEL Windows mpv runtime fully composes the authored Profile 7 FEL source.

**Architecture:** Keep laboratory tooling outside the application and stable runtime. A tested PowerShell module builds immutable experiment invocations and classifies filesystem deltas; a thin runner executes the pinned binary, drives mpv IPC, samples process/GPU state, performs deterministic FEL-on/FEL-off captures, and writes bounded evidence under ignored `.artifacts/native-dv/`. Application integration is a separate conditional plan created only after this proof passes.

**Tech Stack:** PowerShell 7, mpv master/gpu-next, FFmpeg/ffprobe, libplacebo, dovi_tool 2.3.3, Windows named-pipe IPC, NVIDIA SMI, .NET image comparison helper only if mpv output cannot provide stable lossless frames.

**Spec:** `docs/superpowers/specs/2026-09-10-native-zero-disk-dv-p7-design.md`

## Global Constraints

- Branch is `feature/native-zero-disk-dv-p7` from `9de3f8efb271f6643087bd9471a1f5c9dfe03376`; never modify `main`.
- The authored 83,875,552,592-byte MKV is read-only and must retain its length, last-write time, and SHA-256 sentinel samples before and after every experiment.
- Never run the Profile 7 to Profile 8.1 converter or modify its production files.
- Never modify `C:\mpv`, `PATH`, file associations, global mpv configuration, or the stable dependency provisioner.
- Every post-FEL runtime invocation uses an absolute executable, `--no-config`, explicit isolated state paths, and a recorded argument vector and environment.
- Media payload scratch success is exactly zero files and zero bytes; bounded diagnostic/configuration/cache bytes are reported separately by exact path.
- FEL success requires runtime proof of EL decode, BL/EL pairing, and libplacebo composition plus deterministic same-timestamp pixel differences where non-trivial FEL/NLQ contribution exists.
- Do not custom-build until a pinned reputable binary is proven deficient in a named mpv, FFmpeg, or libplacebo requirement.
- Do not publish, release, or tag.

---

### Task 1: Pin upstream ground truth and a candidate Windows runtime

**Files:**
- Create: `docs/native-dv-p7/UPSTREAM-GROUND-TRUTH.md`
- Create: `docs/native-dv-p7/runtime-manifest.json`
- Runtime storage: `.artifacts/native-dv/runtime/<archive-sha256>/`
- Source inspection storage: `.artifacts/native-dv/upstream/{mpv,libplacebo,ffmpeg}/`

**Interfaces:**
- Consumes: approved design and the exact remote heads captured on 2026-09-10.
- Produces: one runtime manifest whose `mpv.executable`, commits, versions, API level, archive hashes, and feature-probe results are the sole runtime identity consumed by later tasks.

- [ ] **Step 1: Fetch exact upstream trees without changing repository state**

Run shallow, detached clones under `.artifacts/native-dv/upstream/` for mpv commit `7e4cb538a3f30d25920ad8e87ba6571540fb729f`, libplacebo commit `3330a515d62139259c26239014f286e233bd3a5c`, and FFmpeg commit `fd7c73d01e976d2e332e85862ab63ab608710834`. Confirm each with `git rev-parse HEAD`.

- [ ] **Step 2: Inspect the merged FEL source path**

Record file-and-line evidence for MKV/in-band splitting, dependent-track creation, the enhancement decoder, PTS pairing/reset behaviour, `mp_image.enhancement_layer`, `vf=format=enhancement-layer`, gpu-next EL mapping/upload, libplacebo enhancement metadata/NLQ composition, required `PL_API_VER`, and FFmpeg `dovi_split`/Dolby Vision stream-group APIs.

- [ ] **Step 3: Confirm release and Windows build status**

Record the latest stable mpv tag, absence or presence of FEL in that tag, current Windows CI/build options, and known Windows/NVIDIA dual-decoder caveats. Primary evidence is upstream source, release refs, and build manifests; issue comments are labelled supporting evidence.

- [ ] **Step 4: Acquire one reputable candidate build**

Download a current zhongfly or shinchiro x86_64 Windows build whose published build metadata pins mpv after `99b4c12`, FFmpeg with `dovi_split`, and libplacebo API at least 370. Save the archive without executing an installer, compute SHA-256, extract only under `.artifacts/native-dv/runtime/<archive-sha256>/`, and record the source release/action URL.

- [ ] **Step 5: Probe binary capabilities**

Run the absolute experimental `mpv.exe` with `--no-config --version`, `--no-config --vf=help`, `--no-config --list-options`, and `--no-config --hwdec=help`. Confirm `gpu-next`, the `format` enhancement-layer option, relevant hardware decoders, and linked dependency versions. If the build fails an exact requirement, record the deficiency and evaluate the next reputable build before considering a custom build.

- [ ] **Step 6: Write and verify the manifest and ground-truth report**

`runtime-manifest.json` contains literal hashes, commits, versions, paths relative to the manifest, URLs, and boolean feature probes. Validate JSON with `Get-Content -Raw | ConvertFrom-Json`, verify every recorded local hash again, and run `git diff --check`.

- [ ] **Step 7: Commit the provenance**

```powershell
git add docs/native-dv-p7/UPSTREAM-GROUND-TRUTH.md docs/native-dv-p7/runtime-manifest.json
git commit -m "docs: pin native Dolby Vision experiment runtime"
```

### Task 2: Build the isolated invocation and storage-delta contract test-first

**Files:**
- Create: `scripts/NativeDvExperiment.psm1`
- Create: `tests/Test-NativeDvExperiment.ps1`
- Modify: `scripts/Test-PowerShellSyntax.ps1` only if its discovery rules do not already include `.psm1` files.

**Interfaces:**
- Consumes: `runtime-manifest.json` and an absolute source path.
- Produces: `New-NativeDvInvocation`, `Get-NativeDvFileSnapshot`, `Compare-NativeDvFileSnapshot`, `Test-NativeDvMediaArtifact`, and `Test-NativeDvSourceIdentity`.

- [ ] **Step 1: Write failing contract tests**

Tests use a temporary fake runtime tree and source. They require FEL-on and FEL-off invocations to contain an absolute non-`C:\mpv` executable, `--no-config`, `--vo=gpu-next`, named-pipe IPC, explicit isolated state/log/cache locations, `--vf=format=enhancement-layer=yes|no`, `--` before the unchanged source path, and no extraction/output/encoding arguments. They require file-delta classification to separate bounded `.log`, `.json`, shader-cache, and watch-later state from `.hevc`, `.mkv`, growing payload caches, or files exceeding the configured bounded-state ceiling. They also require source identity checks to fail on changed length, timestamp, or sentinel hashes.

- [ ] **Step 2: Verify RED**

Run:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File tests/Test-NativeDvExperiment.ps1
```

Expected: failure because `scripts/NativeDvExperiment.psm1` or the named exported functions do not exist.

- [ ] **Step 3: Implement the minimal pure module**

Use immutable `[pscustomobject]` results and `System.Diagnostics.ProcessStartInfo.ArgumentList`; never concatenate a shell command. `New-NativeDvInvocation` accepts `RuntimeManifest`, `SourcePath`, `RunDirectory`, `EnhancementLayer`, `StartSeconds`, `DurationSeconds`, and `PipeName`. Snapshot entries contain absolute path, length, UTC timestamp, and SHA-256 for bounded files. Media-artifact detection uses extensions plus growth/size policy and reports unknown items instead of silently treating them as safe.

- [ ] **Step 4: Verify GREEN and mutation coverage**

Run the test under PowerShell 7 and Windows PowerShell 5.1. Temporarily mutate the module to omit `--no-config`, confirm the test fails, restore it, and rerun both versions.

- [ ] **Step 5: Run repository gates and commit**

Run the 220 Adaptive Media assertions, the new PowerShell test, `scripts/Test-PowerShellSyntax.ps1`, and `git diff --check`.

```powershell
git add scripts/NativeDvExperiment.psm1 tests/Test-NativeDvExperiment.ps1 scripts/Test-PowerShellSyntax.ps1
git commit -m "test: define isolated native Dolby Vision experiment contract"
```

### Task 3: Implement read-only source classification and experiment orchestration test-first

**Files:**
- Create: `scripts/Invoke-NativeDvExperiment.ps1`
- Modify: `scripts/NativeDvExperiment.psm1`
- Modify: `tests/Test-NativeDvExperiment.ps1`
- Create: `docs/native-dv-p7/EXPERIMENT-PROTOCOL.md`

**Interfaces:**
- Consumes: Task 2 invocation/snapshot functions, the pinned runtime, ffprobe/FFmpeg, dovi_tool, `nvidia-smi`, and an absolute source path supplied at execution time.
- Produces: `.artifacts/native-dv/runs/<run-id>/manifest.json`, `classification.json`, `events.jsonl`, `metrics.csv`, `filesystem-before.json`, `filesystem-after.json`, `filesystem-delta.json`, `ipc-snapshots.json`, and bounded logs/captures.

- [ ] **Step 1: Add failing orchestration tests with fake tools**

Fake executables emit fixed version, classification, IPC, and process results into a temporary run directory. Tests require a recorded exact environment/argument vector, no invocation of names matching conversion/extraction/mux output operations, FEL/MEL to remain `Unknown` when NLQ evidence is absent, helper failures to stop before playback, and source identity to be checked before and after every phase.

- [ ] **Step 2: Verify RED**

Run the focused PowerShell test and confirm it fails because the orchestration entry point and classification result do not exist.

- [ ] **Step 3: Implement classification without media-sized files**

Use ffprobe JSON for container/stream topology. Use a bounded or streamed FFmpeg-to-dovi_tool path selected from the pinned tools' documented stdin capabilities; if stdin cannot establish FEL, extract only RPU metadata to the isolated run directory and record its exact bounded size. Never write BL, EL, HEVC, or a media container. Emit tri-state `MelFel` and explicit evidence records rather than inferring FEL from `el_present_flag`.

- [ ] **Step 4: Implement direct-run orchestration**

Launch only the immutable invocation from Task 2. Connect to the named pipe, wait for playback readiness, capture structured properties, sample process CPU/working set and NVIDIA process/GPU memory, execute pause/resume and the documented seek schedule, record command-to-first-stable-frame recovery, and quit through IPC. Always finalize source-identity and filesystem deltas even after cancellation or process failure.

- [ ] **Step 5: Implement deterministic frame capture**

Use identical seek mode, timestamp, output colour parameters, frame count, and lossless output for enhancement-layer on and off. Reject captures when reported playback time/PTS differs beyond the protocol tolerance. Record SHA-256 plus pixel-difference count, maximum channel delta, mean absolute error, and bounding box of changed pixels.

- [ ] **Step 6: Verify GREEN and cleanup failure behaviour**

Run fake-tool tests for success, helper failure, cancellation, changed source identity, media-artifact creation, and an unexpected file. Confirm a created fake `.hevc` makes the run fail even when every helper exits zero.

- [ ] **Step 7: Document and commit the protocol**

Run both PowerShell versions, syntax, whitespace, and existing assertion gates.

```powershell
git add scripts/Invoke-NativeDvExperiment.ps1 scripts/NativeDvExperiment.psm1 tests/Test-NativeDvExperiment.ps1 docs/native-dv-p7/EXPERIMENT-PROTOCOL.md
git commit -m "feat: add zero-disk native Dolby Vision proof harness"
```

### Task 4: Classify the authored source and select FEL timestamps

**Files:**
- Create: `docs/native-dv-p7/AUTHORED-SOURCE-CLASSIFICATION.md`
- Evidence: `.artifacts/native-dv/runs/<classification-run>/`

**Interfaces:**
- Consumes: Task 3 harness and the immutable authored source.
- Produces: definitive or explicitly unknown P7/MEL/FEL classification plus at least one reproducible candidate timestamp with non-trivial FEL/NLQ evidence.

- [ ] **Step 1: Record pre-run identity**

Record full path redacted in committed docs, exact byte length, last-write time, first/middle/last 16 MiB sentinel SHA-256 values, and source volume free bytes. Do not compute a full-file hash unless sequential read cost is justified and no new file is written.

- [ ] **Step 2: Run independent classification probes**

Capture ffprobe, dovi_tool, and experimental mpv diagnostics. Resolve tool interpretation differences explicitly. Establish P7, compatibility ID, HDR10 base, BL/RPU/EL, stream topology, and MEL/FEL state.

- [ ] **Step 3: Find non-trivial FEL/NLQ evidence**

Use dovi_tool level-2/NLQ metadata or equivalent source evidence to select one or more timestamps. If the source is MEL or contribution remains unproven, set the proof gate to failed and skip Task 5 FEL certification.

- [ ] **Step 4: Verify post-run identity and storage delta**

Rerun identity checks and report every created file and byte. Media payload artifacts must be zero.

- [ ] **Step 5: Commit the classification report**

```powershell
git add docs/native-dv-p7/AUTHORED-SOURCE-CLASSIFICATION.md
git commit -m "docs: classify authored Profile 7 source"
```

### Task 5: Execute FEL-on/FEL-off, zero-disk, memory, and seek proof

**Files:**
- Create: `docs/native-dv-p7/DIRECT-PLAYBACK-EVIDENCE.md`
- Create: `docs/native-dv-p7/direct-playback-summary.json`
- Evidence: `.artifacts/native-dv/runs/<fel-on-run>/` and `.artifacts/native-dv/runs/<fel-off-run>/`

**Interfaces:**
- Consumes: pinned runtime, classified FEL source, selected timestamps, and Task 3 harness.
- Produces: the explicit application-integration gate `supported`, `unsupported`, or `unknown`, with all measured metrics and evidence references.

- [ ] **Step 1: Run FEL enabled with the preferred hardware decoder**

Capture decoder selection for BL and EL, pairing, RPU, composition, renderer/API, process/GPU metrics, frame timings, drops, startup, pause/resume, seek reset/recovery, filesystem delta, and source identity.

- [ ] **Step 2: Run hardware-decoder alternatives narrowly**

If NVDEC does not keep both layers in hardware, test the existing D3D11VA/D3D11 path with otherwise identical settings. Record request versus observation and any fail-soft software or BL-only fallback.

- [ ] **Step 3: Run FEL disabled at identical timestamps**

Keep every variable except `enhancement-layer=no` identical. Confirm the disabled path does not claim composition and record whether the EL decoder still exists upstream despite renderer suppression.

- [ ] **Step 4: Compare deterministic captures**

Require matching playback timestamps and nonzero objective pixel differences in the authored contribution region. Renderer evidence and pixel differences are both mandatory for `supported`.

- [ ] **Step 5: Prove bounded resource behaviour**

Compare early and late sustained samples plus the short FEL fixture. Report average/peak CPU, working set, GPU utilisation, dedicated/shared memory where observable, decoder engines, frame timings, drops, startup and seek recovery. Memory must remain bounded after seeks and over playback duration.

- [ ] **Step 6: Prove zero media-sized disk state**

Aggregate launch, sustained playback, pause/resume, seek, and exit deltas for mpv and helpers. List exact bounded-state paths/bytes. Fail the gate for any extracted/converted video, temporary media container, growing payload cache, shadow file, or unattributed large write.

- [ ] **Step 7: Decide and document the integration gate**

Set `supported` only when runtime path proof and deterministic A/B proof both pass. Otherwise set `unsupported` or `unknown` with the exact missing observation; never infer success from playback exit code.

- [ ] **Step 8: Verify and commit evidence summaries**

Validate summary JSON, rerun source identity, `git diff --check`, and the complete 339-assertion validation gate.

```powershell
git add docs/native-dv-p7/DIRECT-PLAYBACK-EVIDENCE.md docs/native-dv-p7/direct-playback-summary.json
git commit -m "docs: record native Dolby Vision playback proof"
```

### Task 6: Conditional handoff to product integration

**Files:**
- Create only when Task 5 is `supported`: `docs/superpowers/plans/2026-09-10-native-dv-experimental-integration.md`
- Create when Task 5 is not `supported`: `docs/native-dv-p7/REMAINING-BLOCKER.md`

**Interfaces:**
- Consumes: the machine-readable Task 5 integration gate.
- Produces: either a test-first application integration plan using only proven observations, or an exact blocker report with no application production changes.

- [ ] **Step 1: Enforce the evidence gate**

If the result is not `supported`, do not modify application runtime selection, planner, UI, or diagnostics. Record the single smallest experiment or upstream diagnostic property needed next.

- [ ] **Step 2: Map proven runtime facts into typed interfaces**

For a supported result, define exact C# records and IPC/property sources for requested profile/FEL/runtime/renderer/hardware decode and observed BL decoder, EL decoder, pairing, composition, RPU, renderer/API, surfaces, fallback, media scratch, memory, and timing. Every field without a structured source is nullable/unknown or explicitly backed by the narrowly versioned diagnostic adapter.

- [ ] **Step 3: Write the separate integration plan**

The plan must cover test-first runtime-manifest selection, planner isolation, no-conversion/no-scratch invariants, observed-state reduction, diagnostics, minimal developer UI, cancellation/cleanup, stable runtime isolation, full validation, installer impact assessment, and real Adaptive Media hardware proof.

- [ ] **Step 4: Commit the gate artifact**

```powershell
git add docs/superpowers/plans/2026-09-10-native-dv-experimental-integration.md docs/native-dv-p7/REMAINING-BLOCKER.md
git commit -m "docs: gate experimental native Dolby Vision integration"
```

Only the artifact that exists for the observed outcome is staged.
