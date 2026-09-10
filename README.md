# Adaptive Media

Adaptive Media is a Windows 11 reference-first launcher around
**mpv / gpu-next / libplacebo**, with optional per-video enhancements and an
MPC-BE fallback.

This is the standalone Adaptive Media repository. The application, tests,
installer, payload, documentation, CI, and future release-candidate tooling no
longer depend on the `localai-windows-starter` repository layout.

## Repository layout

- `src/AdaptiveMedia.App/` — native .NET 10 WPF application
- `tests/` — planner, settings, packaging, reconstruction, and Dolby Vision
  execution gates
- `installer/` — Inno Setup definition
- `payload/` — playback engine, dependency provisioner, icon, and mpv config
- `scripts/` — build and guarded release tooling
- `docs/` — product, contract, migration provenance, and historical records
- `legacy/0.3.x/` — inactive historical source transport and reconstruction
  material

## Verified gates

From the repository root:

```powershell
dotnet run --project tests/AdaptiveMedia.Tests/AdaptiveMedia.Tests.csproj -c Release
dotnet run --project tests/SettingsTests/SettingsTests.csproj -c Release
dotnet run --project tests/DolbyVisionTests/DolbyVisionTests.csproj -c Release
pwsh -NoProfile -ExecutionPolicy Bypass -File tests/Test-Packaging.ps1
pwsh -NoProfile -ExecutionPolicy Bypass -File tests/Test-Reconstruction.ps1
pwsh -NoProfile -ExecutionPolicy Bypass -File tests/Run-DolbyVisionExecutionTests.ps1
dotnet restore src/AdaptiveMedia.App/AdaptiveMedia.App.csproj
dotnet build src/AdaptiveMedia.App/AdaptiveMedia.App.csproj -c Release --no-restore
```

The Dolby Vision execution runner requires FFmpeg/FFprobe 7.1 and prepares
pinned dovi_tool 2.3.3 and MKVToolNix 101.0 under ignored `.artifacts/` storage.
The checked-in MEL/FEL fixtures and their upstream license/provenance are under
`tests/DolbyVisionExecutionTests/fixtures/`.

Build the application and installer with:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/Build-Dev.ps1 -Clean
```

Add `-SmokeTest` for the isolated install, self-test, integration-test, upgrade,
uninstall, and settings-preservation gate.

## Release policy

- Reference playback does not silently enable interpolation, fake HDR,
  aggressive sharpening, or cleanup.
- High-quality scaling, smooth motion, debanding, RTX Video Super Resolution,
  and RTX Video HDR remain explicit opt-ins.
- Decoded PCM is the safe audio default; HDMI bitstream remains optional.
- Dolby Vision metadata can be processed by mpv/libplacebo, but Adaptive Media
  does not claim native Windows Profile 7 FEL passthrough.

Future candidate builds are manual-only. Release operators must pass the certified
installer digest and expected commit to `scripts/Publish-Release.ps1`; when supplied,
the script verifies them and independently verifies published assets. Migration does
not publish or recreate any release.

## Provenance and licensing

Adaptive Media historically originated in
[`allusionsafk/localai-windows-starter`](https://github.com/allusionsafk/localai-windows-starter).
The original `v0.4.0-rc1` prerelease remains there. Extraction details, source
commit/tree identifiers, rewritten-history disclosure, and the migration path
map are recorded in [docs/MIGRATION-PROVENANCE.md](docs/MIGRATION-PROVENANCE.md).

No project-level license existed at the migration checkpoint, and this
repository does not invent one. Third-party fixture notices are preserved with
the fixtures.
