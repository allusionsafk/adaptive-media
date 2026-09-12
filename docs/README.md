# Adaptive Media documentation

This directory separates current product and technical references from historical engineering material.

Start with the repository [README](../README.md) for the project overview.

## Current references

| Document | Purpose |
|---|---|
| [PRODUCT.md](PRODUCT.md) | Product and interface principles |
| [DOLBY-VISION-CONTRACT.md](DOLBY-VISION-CONTRACT.md) | Supported Dolby Vision behaviour |
| [ROADMAP-0.4.md](ROADMAP-0.4.md) | Current 0.4 engineering roadmap |
| [MIGRATION-PROVENANCE.md](MIGRATION-PROVENANCE.md) | Standalone-repository migration record |

## Release records

`releases/` contains release notes and candidate records. A release record describes the named candidate only and does not prove that later development has shipped.

## Engineering plans

`superpowers/specs/` and `superpowers/plans/` contain implementation specifications and execution plans used during development.

They record design intent and review history. When an older plan disagrees with current code, tests, accepted contracts, or release evidence, use the current verified state.

## Historical material

`OLD-REPOSITORY-CLEANUP-PROPOSAL.md` and `legacy/` document earlier repository structure and migration work. They are retained for provenance, not as current setup instructions.

## Documentation rules

Public documentation should:

- distinguish requested behaviour from observed runtime state
- state the test or evidence behind technical claims
- mark experimental and historical work clearly
- avoid unsupported format, hardware, or release claims
- keep machine-specific paths, private media, identifiers, and credentials out of committed examples
- use Adaptive Media as the repository product name until a separate naming decision is made
