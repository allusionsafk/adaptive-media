# Contributing to Adaptive Media

Adaptive Media is still defining its supported Windows path. Contributions are most useful when they improve playback correctness, observability, recovery, packaging, or test coverage without weakening the reference-first default.

## Before changing behaviour

1. Reproduce the problem or capture the current baseline.
2. Identify the smallest relevant contract or test.
3. Make the focused change.
4. Run the narrow checks first, then the wider repository gates that cover the touched area.
5. Update documentation only for behaviour that is actually implemented or measured.

Keep unrelated refactors, dependency churn, renames, and visual cleanup out of a focused fix.

## Product rules

Preserve these boundaries unless a change explicitly proposes and reviews a new one:

- reference playback stays conservative
- interpolation and synthetic enhancement remain explicit choices
- requested settings are not reported as observed runtime state
- unsupported or degraded paths are explained rather than hidden
- original media is not modified as a side effect of playback
- release claims follow evidence, not intent

## Tests

Use the checks relevant to your change. The root [README](README.md) lists the current build and test commands.

Playback, packaging, reconstruction, and Dolby Vision changes should include the corresponding regression gate. A documentation-only change does not need heavyweight media execution unless it changes a contract consumed by tests.

## Pull requests

A good pull request states:

- the concrete problem
- what changed
- what did not change
- the checks that ran and their results
- any remaining limitation or unverified path

Call out changes to media handling, network access, installation, release tooling, privacy, or third-party licensing.

## Security

Do not put private media, credentials, tokens, machine identifiers, personal paths, or unrelated diagnostic data in issues, tests, screenshots, or fixtures.

Security reports belong in the private path described in [SECURITY.md](SECURITY.md).
