# Adaptive Media

Adaptive Media is a Windows 11 media player built around reference playback, explicit enhancement, and observable pipeline state.

It uses a native .NET 10 WPF application with mpv, gpu-next, libplacebo, FFmpeg, and an MPC-BE fallback. The default path aims to play the source faithfully. Scaling, motion smoothing, debanding, RTX Video Super Resolution, and RTX Video HDR remain explicit choices.

## Status

Active development.

The standalone repository contains the application, tests, installer definition, playback payload, documentation, and release tooling. The historical `v0.4.0-rc1` prerelease remains in the former shared repository and has not been republished here.

## Playback principles

- Reference playback does not silently enable interpolation, fake HDR, aggressive sharpening, or cleanup.
- Decoded PCM is the safe audio default. HDMI bitstream output is optional.
- Requested settings and observed runtime state are treated separately.
- Hardware and media-specific fallbacks should be visible instead of silently presented as success.
- Enhancement features should fail back to a dependable playback path.

## Dolby Vision work

The repository includes provenance-backed MEL and FEL fixtures plus execution tests for the current Dolby Vision conversion path.

The implemented conversion work classifies the source from whole-stream evidence, performs transactional Profile 7 to Profile 8.1 processing where supported, and bounds temporary media scratch space. This is not a claim of native Windows Profile 7 FEL passthrough.

See [the Dolby Vision contract](docs/DOLBY-VISION-CONTRACT.md) for the exact boundary.

## Build and test

From the repository root:

```powershell
dotnet restore src/AdaptiveMedia.App/AdaptiveMedia.App.csproj
dotnet build src/AdaptiveMedia.App/AdaptiveMedia.App.csproj -c Release --no-restore

dotnet run --project tests/AdaptiveMedia.Tests/AdaptiveMedia.Tests.csproj -c Release
dotnet run --project tests/SettingsTests/SettingsTests.csproj -c Release
dotnet run --project tests/DolbyVisionTests/DolbyVisionTests.csproj -c Release

pwsh -NoProfile -ExecutionPolicy Bypass -File tests/Test-Packaging.ps1
pwsh -NoProfile -ExecutionPolicy Bypass -File tests/Test-Reconstruction.ps1
pwsh -NoProfile -ExecutionPolicy Bypass -File tests/Run-DolbyVisionExecutionTests.ps1
```

The Dolby Vision execution runner requires FFmpeg and FFprobe 7.1. It prepares pinned copies of dovi_tool 2.3.3 and MKVToolNix 101.0 under ignored `.artifacts/` storage.

Build the application and installer with:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/Build-Dev.ps1 -Clean
```

Add `-SmokeTest` for the isolated install, self-test, integration-test, upgrade, uninstall, and settings-preservation gate.

## Repository layout

| Path | Purpose |
|---|---|
| `src/AdaptiveMedia.App/` | Native Windows application |
| `tests/` | Playback planning, settings, packaging, reconstruction, and Dolby Vision gates |
| `payload/` | Playback engine, dependency provisioner, icon, and mpv configuration |
| `installer/` | Inno Setup definition |
| `scripts/` | Build and guarded release tooling |
| `docs/` | Product, contracts, release records, and migration provenance |
| `legacy/0.3.x/` | Inactive historical source and reconstruction material |

## Documentation

Start with [docs/README.md](docs/README.md).

Key references:

- [Product principles](docs/PRODUCT.md)
- [Dolby Vision contract](docs/DOLBY-VISION-CONTRACT.md)
- [0.4 roadmap](docs/ROADMAP-0.4.md)
- [Migration provenance](docs/MIGRATION-PROVENANCE.md)
- [Contributing](CONTRIBUTING.md)
- [Support](SUPPORT.md)
- [Security](SECURITY.md)

## Release policy

Candidate publishing is manual and guarded. A release operator supplies the expected commit and certified installer digest to `scripts/Publish-Release.ps1`; the script verifies those inputs and verifies the published assets again.

Migration to this repository did not publish, recreate, or retag a release.

## Licensing

No project-level licence existed at the migration checkpoint, so this repository does not invent one. Third-party fixture notices remain with their fixtures. A project licence should be chosen deliberately before broader distribution.

---

**ALLUSIONS**  
Independent software by Jidan.  
[@allusionsafk](https://github.com/allusionsafk)
