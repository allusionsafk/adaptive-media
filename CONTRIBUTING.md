# Contributing to Adaptive Media

Adaptive Media is still defining its supported Windows path. Contributions should improve playback correctness, recovery, packaging, diagnostics, or test coverage without weakening the reference-first default.

## Before changing behaviour

1. Reproduce the problem or capture the current baseline.
2. Identify the smallest relevant contract or test.
3. Make a focused change.
4. Run narrow checks first, then the wider repository checks for the area you changed.
5. Update documentation only for behaviour that is implemented or measured.

Keep unrelated refactors, dependency churn, renames, and visual cleanup out of a focused fix.

## Product rules

Preserve these rules unless the change explicitly proposes a new one:

- reference playback stays conservative
- interpolation and synthetic enhancement remain explicit choices
- requested settings are not reported as observed runtime state
- unsupported or degraded paths are explained
- original media is not modified as a side effect of playback
- release claims follow test and release evidence

## Tests

Use the checks relevant to your change. The root [README](README.md) lists the current build and test commands.

Playback, packaging, reconstruction, and Dolby Vision changes should include the corresponding regression check. Documentation-only changes do not need heavyweight media execution unless they change a contract used by tests.

## Pull requests

A pull request should state:

- the problem
- the change
- what remains unchanged when that matters to review
- checks run and their results
- known limitations or unverified paths

Call out changes to media handling, network access, installation, release tooling, privacy, or third-party licensing.

## Security

Do not put private media, credentials, tokens, machine identifiers, personal paths, or unrelated diagnostic data in issues, tests, screenshots, or fixtures.

Report vulnerabilities through the private path in [SECURITY.md](SECURITY.md).
