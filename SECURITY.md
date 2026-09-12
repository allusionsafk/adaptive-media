# Security policy

Security and privacy reports are welcome when Adaptive Media could put a user's machine, media, or data at risk.

## Report privately

Use GitHub private vulnerability reporting:

https://github.com/allusionsafk/adaptive-media/security/advisories/new

Do not publish exploit details, credentials, private media, personal paths, tokens, or sensitive logs in a normal issue.

## In scope

Useful reports include:

- unsafe file handling or command construction
- unintended modification or deletion of source media
- privilege or installer behaviour that exceeds the documented boundary
- dependency or update paths that execute unverified content
- local service exposure outside the intended boundary
- diagnostic or log output that leaks private media paths or content
- release integrity problems

## What to include

Provide the smallest reproducible report you can:

1. commit or build tested
2. Windows version
3. only the hardware details relevant to the issue
4. exact reproduction steps
5. expected behaviour
6. observed behaviour
7. likely impact
8. a sanitised log excerpt when necessary

For non-security playback and compatibility problems, use [SUPPORT.md](SUPPORT.md).
