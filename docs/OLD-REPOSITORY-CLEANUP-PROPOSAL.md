# Proposed cleanup for `localai-windows-starter`

This is a proposal only. None of these steps were performed by the standalone
repository migration.

After the standalone repository and its CI remain green, prepare a separate,
reviewed change in `allusionsafk/localai-windows-starter` that:

1. replaces the active `adaptive-media/` source subtree with a short archival
   pointer to `https://github.com/allusionsafk/adaptive-media` and the exact
   migration source commit;
2. updates the root README and product/distribution wording so Adaptive Media is
   no longer presented as an actively developed component of the AFK AI product;
3. retires Adaptive Media build, candidate, and release workflows from
   `.github/workflows/` after confirming no AFK workflow depends on them;
4. updates or retires the root Adaptive Media dev-kit/update launchers and
   `manifest.json` entries that fetch from shared-repository branches;
5. keeps the historical `v0.4.0-rc1` release, assets, tag, release notes, and
   links intact, with wording that the release predates the repository split;
6. reviews stale Adaptive Media branches for archival naming or protection, but
   does not delete them without separate approval; and
7. preserves provenance references needed to trace the old release and the
   extraction checkpoint.

Before executing that cleanup, verify links against the final standalone
default branch and release policy. Branch deletion, tag deletion, release
deletion, and source-subtree removal remain separately destructive actions.
