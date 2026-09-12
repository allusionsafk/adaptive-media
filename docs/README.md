# Adaptive Media documentation

This directory separates current product contracts from historical engineering records.

For the project overview, start with the repository [README](../README.md).

## Current product references

| Document | Purpose |
|---|---|
| [PRODUCT.md](PRODUCT.md) | Product and interface principles |
| [DOLBY-VISION-CONTRACT.md](DOLBY-VISION-CONTRACT.md) | Dolby Vision behaviour and claim boundary |
| [ROADMAP-0.4.md](ROADMAP-0.4.md) | Current 0.4 engineering roadmap |
| [MIGRATION-PROVENANCE.md](MIGRATION-PROVENANCE.md) | Standalone-repository extraction and provenance |

## Release records

`releases/` contains release notes and historical candidate records.

Release notes describe the state of a named candidate. They should not be treated as proof that later work has shipped.

## Engineering plans

`superpowers/specs/` and `superpowers/plans/` contain implementation specifications and execution plans used during development.

These records are useful for design intent and review history. Current code, tests, accepted contracts, and release evidence take precedence when they disagree with an older plan.

## Historical material

`OLD-REPOSITORY-CLEANUP-PROPOSAL.md` and material under `legacy/` document earlier repository structure and migration work. They are retained for provenance, not as current setup instructions.

## Documentation standard

Public documentation should:

- distinguish requested behaviour from observed runtime state
- state the exact test or evidence behind a technical claim
- mark experimental and historical work clearly
- avoid unsupported format, hardware, or release claims
- keep machine-specific paths, private media, identifiers, and credentials out of committed examples
- use Adaptive Media as the repository product name until a separate naming decision is made
