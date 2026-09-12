# Adaptive Media support

Adaptive Media is active development software. The standalone repository has not yet published a replacement for the historical prerelease hosted in the former shared repository.

## Report a problem

Use a GitHub issue for reproducible playback, packaging, settings, or compatibility problems.

Include only what is needed:

- commit or build tested
- Windows version
- relevant GPU and driver details
- source container and codec information when the problem is media-specific
- exact observed behaviour
- expected behaviour
- the smallest useful log or diagnostic excerpt

For playback issues, note whether the problem also occurs with the repository's reference mpv path when that comparison is available.

## Keep reports private when needed

Do not upload copyrighted source media, private files, credentials, tokens, personal paths, or unrelated machine information.

For a security or privacy issue, follow [SECURITY.md](SECURITY.md) instead of opening a public issue.

## Current support boundary

The current repository targets Windows 11. Other operating systems and broader hardware matrices are not advertised as supported until they have their own tested installation and playback path.

The project does not promise playback of DRM-protected media or every possible codec, container, driver, and display combination. A useful report identifies the exact input and the path that failed.
