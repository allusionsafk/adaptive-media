# Native Dolby Vision Runtime Lifecycle Plan

**Base:** `dd241db28cf5a284590751b6f184a6e9cae5eebb` on `feature/native-dv-runtime-lifecycle`.

**Goal:** Turn the single-generation runtime store into a versioned, recoverable
lifecycle: move between supported generations, retain one known-good fallback,
collect genuinely superseded ones, and never let a runtime bump quietly degrade
what the application claims to have observed.

**Not in scope:** re-proving FEL, the pixel A/B, upstream archaeology, or
reimplementing `NativeDvRuntimeStore`. It already does download, verify, extract,
validate, atomic promotion, and revalidation, and is extended rather than replaced.

## The defect that motivates the adapter work

`PlaybackService` reduces observation with
`NativeDvLogEvidence.Reduce(log, version, NativeDolbyVision.Descriptor.MpvCommit, …)`.
The commit the adapter trusts therefore comes from the **shipped manifest**. Bump
the manifest and the adapter silently accepts the new build, applying parser
patterns nobody verified against it. A runtime update could then turn a requested
Full FEL into a claimed Full FEL. This inverts the safety property the version
pin was introduced for, and fixing it is the centre of this milestone.

## 1. Generation identity

Unchanged and content-addressed: a generation is `<root>/<archive-sha256>`, whose
contents are verified by exact length and SHA-256 per component. Files inside an
installed generation are never mutated. A different archive is a different
generation.

## 2. Current, candidate, previous

- **Current** — the generation matching the shipped descriptor, once validated.
- **Candidate** — a generation being provisioned, living only in staging until it
  validates.
- **Previous** — the generation that was current before the last successful
  promotion, retained as a known-good fallback.

Recorded in `<root>/state.json` (`schemaVersion`, `current`, `previous`,
`updatedAtUtc`). The file is advisory: it may name only generations that exist and
validate, and any id that is not 64 hex characters or that resolves outside the
root is ignored rather than trusted.

## 3. Adapter compatibility

A new `NativeDvDiagnosticAdapters` table lists the mpv commits whose diagnostic
parsing contract has actually been verified. Support is decided by looking up the
**observed** runtime version in that table — never by the descriptor. An
unverified commit yields an explicit unsupported result, observation stays
Unknown, and the lane falls back truthfully. Adding a runtime therefore requires
a deliberate entry, not a manifest edit.

## 4. Promotion

Unchanged: staging → full validation → single `Directory.Move`. Promotion then
updates `state.json`, demoting the outgoing current to previous.

## 5. Failure and rollback

Any failure — download, length, hash, extraction, component mismatch, unsupported
runtime, cancellation, crash, promotion race, locked files — must leave the
existing current generation untouched and usable. Nothing is deleted before a
replacement is proven. A failed update reports itself and retains what worked.

## 6. Garbage collection

GC deletes a generation only when all hold: it is inside the app-owned root; its
name is a 64-hex generation id; its resolved path is a direct child of the root;
it is not current, previous, or in flight; and it is not locked. Staging is
cleaned under the same root check. No path from the manifest or state file is
ever recursively deleted on trust.

Retention: current plus previous known-good.

## 7. Concurrency

A single cross-process exclusive lock file at `<root>/.lock`, held for
provisioning, promotion, and GC. `Resolve` stays lock-free because it is
read-only. A process that cannot take the lock reports a busy state rather than
racing. This is deliberately the lightest mechanism that is correct on Windows.

## 8. User-visible state

A small typed lifecycle state for the existing developer setting and diagnostics:
not installed, installing, ready, update available, updating, update failed with
previous retained, unsupported runtime, verification failed, busy, cleanup
pending. No Settings redesign.

## 9. Runtime upgrade evaluation

`zhongfly/mpv-winbuild` has published mpv commit `14f2d48cbc` after the pinned
`7e4cb538a3`. It is evaluated as a real candidate: provenance captured, archive
verified against the publisher digest, Profile 7 splitting, dual decode, pairing
and NLQ composition confirmed present, and the diagnostic patterns re-verified
against that exact build. It is adopted only if all of that holds; otherwise the
known-good runtime stays pinned and the reason is recorded. Freshness is
subordinate to correctness.

## 10. Tests and gate

Targeted regressions for: A→B upgrade; corrupt or cancelled B leaving A usable;
unsupported commit never promoted as Full FEL; no re-download when valid; GC
eligibility including refusal to delete current or previous; escaping state paths
refused; stale staging cleanup; real concurrent provisioners; restart
reconciliation; adapter version selection; Unknown never promoted. Then one
comprehensive repository gate, commit, and push. No merge, tag, or release.
