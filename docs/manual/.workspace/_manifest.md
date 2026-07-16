# Manual Manifest

**Outline:** `_outline.md` (created 2026-07-16 at commit `f0542d7`)
**Last updated:** 2026-07-16
**Phase:** complete

| Path | Status | Verified at |
|---|---|---|
| README.md | done | f0542d7 2026-07-16 |
| 00-overview.md | done | f0542d7 2026-07-16 |
| 01-geometry.md | done | f0542d7 2026-07-16 |
| 02-network-validation.md | done | f0542d7 2026-07-16 |
| 03-junctions-control.md | done | f0542d7 2026-07-16 |
| 04-lane-graph-connectors.md | done | f0542d7 2026-07-16 |
| 05-traffic-sim.md | done | f0542d7 2026-07-16 |
| 06-drafting-snapping.md | done | f0542d7 2026-07-16 |
| 07-rendering-markings.md | done | f0542d7 2026-07-16 |
| 08-persistence.md | done | f0542d7 2026-07-16 |
| glossary.md | done | f0542d7 2026-07-16 |

## Phase 6 (controller) — completed 2026-07-16

- **glossary.md** compiled from `_terminology.md` + all `_notes.ch0*.md` terminology
  entries — 60 terms, alphabetized, each linked to its best chapter section. Deduplicated
  (e.g. NodeReuseRadius vs absorption reconciled; stranded-lane iff-rule canonicalized to
  ch. 02). Reconciled against code where chapters differed.
- **00-overview.md** written: architecture Mermaid + per-subsystem paragraphs, the
  draft→validate→commit→derived-rebuild→resync data flow, reading guide, conventions,
  quality stack, a compiled known-issues/[UNCERTAIN] list, and the skip list.
- **README.md** written: book cover, audience, freshness stamp, TOC, drift-update policy.
- **Cross-reference resolution:** 27 relative chapter→chapter links added at the
  navigation points (At-a-glance "Depends on/Used by" lines and opening prose) for every
  bare "ch. 0X" forward-ref recorded in the notes. Prose otherwise untouched.
- **Coherence fixes (verified against code):**
  1. ch04 at-a-glance said `ConnectorBuilder.cs` is "231 lines"; `wc -l` = 301. Fixed to
     301 (its own `cs:289-300` refs already assumed >231).
  2. ch05 *How to verify* labeled "13/40 minor arrivals" — 13 is the assertion *floor*
     (`MinorDischargeFloor`), the M5 *measured* value is 18/40 (baseline 7/40, per
     `AssertivenessGuardTests.cs:166-176`). Reworded to distinguish floor from measured.
  - Surfaced (not edited — out of manual scope): `docs/architecture.md` still lists 4 road
    types; code + chapters have 6 (OneWay, Asymmetric added M5). Flagged in
    00-overview "Known issues → Cross-cutting doc drift".
- Every chapter's `[UNCERTAIN]` flags left intact and compiled into 00-overview.
- `.workspace/` files retained (not deleted).
