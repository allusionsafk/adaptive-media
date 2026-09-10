# Migration provenance

Adaptive Media was extracted from the public
[`allusionsafk/localai-windows-starter`](https://github.com/allusionsafk/localai-windows-starter)
repository on 2026-09-09.

| Field | Value |
|---|---|
| Source branch | `astra/dv-conversion-contract-20260909` |
| Source commit | `ddc5cdc7e85e3a8949de71fb093a49dd20180864` |
| Source `adaptive-media/` tree | `9680c8a80229fd0fac467d5973ea3659bed228d9` |
| Extraction command | `git subtree split --prefix=adaptive-media ddc5cdc7e85e3a8949de71fb093a49dd20180864` |
| Initial split commit | `33916bad8d05ecbafc99d5cac4072f15be237a14` |
| Sanitized split commit | `652d60085e4f8c99f1ba521e1b95815052cbbc63` |
| Extraction style | Adaptive Media-specific history, rewritten by subtree extraction |

The initial split commit's tree exactly matched the source subtree tree above.
Commit SHA continuity with the source repository is intentionally not claimed:
subtree extraction rewrote the Adaptive Media commits, and a publication-safety
rewrite changed nine descendant SHAs after replacing two occurrences of a
private Windows development path in one historical implementation plan with a
portable `%TEMP%`-based command. No application or conversion source changed in
that rewrite.

The local extraction clone initially inherited tags from the source repository.
Those refs included unrelated AFK AI history and the historical
`v0.4.0-rc1` tag. They were removed before publication. No historical tag is
being moved, recreated, or republished by this repository migration.

## Historical release truth

Adaptive Media `v0.4.0-rc1` was built and published from
`allusionsafk/localai-windows-starter`, source commit
`37f33470c908ec4c9a3e1cd47791505cd7281165`. Its original prerelease and assets
remain at the
[`localai-windows-starter` v0.4.0-rc1 release](https://github.com/allusionsafk/localai-windows-starter/releases/tag/v0.4.0-rc1).
This repository does not claim to have produced that release.

## Migration path mapping

| Source path at the extraction commit | Standalone path |
|---|---|
| `adaptive-media/source/src/` | `src/` |
| `adaptive-media/source/tests/` | `tests/AdaptiveMedia.Tests/` |
| `adaptive-media/source/installer/` | `installer/` |
| `adaptive-media/source/payload/` | `payload/` |
| `adaptive-media/source/scripts/` | `scripts/` |
| `adaptive-media/tests/` | `tests/` |
| `adaptive-media/DOLBY-VISION-CONTRACT.md` | `docs/DOLBY-VISION-CONTRACT.md` |
| historical 0.3.x transport and reconstruction files | `legacy/0.3.x/` |

The source repository's root `.gitignore` was the only repository-wide file
needed by the standalone tree. Its relevant generated-output rules were adapted
to the flattened paths. The old repository's Adaptive Media workflows were
historical release/reconstruction machinery coupled to the shared layout; they
were not copied as active workflows. Standalone CI and manual candidate
automation were created for this repository instead.

No project-level license existed at the extraction checkpoint, so none was
invented during migration. Required third-party fixture notices remain under
`tests/DolbyVisionExecutionTests/fixtures/`.
