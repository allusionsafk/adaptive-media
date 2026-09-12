# Adaptive Media

Adaptive Media is a Windows 11 media player focused on dependable playback with optional enhancement controls.

The application is built with .NET 10 WPF and uses mpv, gpu-next, libplacebo, FFmpeg, and an MPC-BE fallback. Reference playback is the default. Scaling, motion smoothing, debanding, RTX Video Super Resolution, and RTX Video HDR are explicit user choices.

[Documentation](docs/README.md) | [Support](SUPPORT.md) | [Security](SECURITY.md) | [Contributing](CONTRIBUTING.md)

## Status

Active development.

This standalone repository contains the application, tests, installer definition, playback payload, documentation, and release tooling. The historical `v0.4.0-rc1` prerelease remains in the former shared repository and has not been republished here.

## Playback policy

- Reference playback does not silently enable interpolation, synthetic HDR, aggressive sharpening, or cleanup.
- Decoded PCM is the safe audio default. HDMI bitstream output is optional.
- Requested settings are kept separate from observed runtime state.
- Unsupported or degraded paths should be visible to the user.
- Enhancement failures should fall back to a dependable playback path when possible.

## Dolby Vision

The repository includes provenance-backed MEL and FEL fixtures and execution tests for the current Dolby Vision conversion path.

Supported conversion work classifies the source from whole-stream evidence and performs transactional Profile 7 to Profile 8.1 processing where supported. Temporary media scratch space is bounded. Native Windows Profile 7 FEL passthrough is not claimed.

See [DOLBY-VISION-CONTRACT.md](docs/DOLBY-VISION-CONTRACT.md) for the exact supported behaviour.

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

The Dolby Vision execution runner requires FFmpeg and FFprobe 7.1. It prepares pinned copies of dovi_tool 2.3.3 and MKVToolNix 101.0 under the ignored `.artifacts/` directory.

Build the application and installer with:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/Build-Dev.ps1 -Clean
```

Add `-SmokeTest` to run the isolated install, self-test, integration-test, upgrade, uninstall, and settings-preservation gate.

## Repository layout

| Path | Purpose |
|---|---|
| `src/AdaptiveMedia.App/` | Native Windows application |
| `tests/` | Playback, settings, packaging, reconstruction, and Dolby Vision tests |
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

## Releases

Candidate publishing is manual and guarded. The release operator supplies the expected commit and certified installer digest to `scripts/Publish-Release.ps1`. The script verifies those inputs and checks the published assets again.

Migration to this repository did not publish, recreate, or retag a release.

## Licensing

No project-level licence existed at the migration checkpoint, so this repository does not add one retroactively. Third-party fixture notices remain with their fixtures. A project licence should be chosen before broader distribution.

**ALLUSIONS**  
Independent software by Jidan.
