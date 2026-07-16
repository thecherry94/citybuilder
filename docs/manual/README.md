# The citybuilder Manual

A living reference manual for the **citybuilder** codebase — a Cities: Skylines 2–inspired
city builder in Godot 4.6 (mono / C#), built milestone by milestone toward a sellable game.
Its road network and traffic simulation are the foundation everything else grows on, and
they are what this manual explains in depth.

## Who this is for

An experienced game/sim programmer who has just joined the project — comfortable with C#,
Godot basics, and general traffic-sim concepts — and cannot ask the original author. The
manual is written to be self-sufficient: it explains not just *what* the code does but
*why* each non-obvious decision was made, which bugs shaped it, and how to verify a change.
It **complements** the shorter reference docs ([architecture](../architecture.md),
[conventions](../conventions.md), [verification](../verification.md),
[gotchas](../gotchas.md), [roadmap](../roadmap.md)) with per-subsystem, book-shaped depth —
read those for the quick lookup, this for the understanding.

## Freshness

**Verified against commit `f0542d7`, 2026-07-16 (milestone M6).** Every chapter carries its
own "Last verified against commit" stamp. If the code has moved since, treat file:line
anchors as approximate and the prose as a strong prior, not gospel.

## Table of contents

Start with **[00 · Overview](00-overview.md)** for the architecture, the reading guide, the
conventions, and the full known-issues list. Then read the chapter matching your task.

| # | Chapter | One line |
|---|---|---|
| 00 | [Overview & reading guide](00-overview.md) | What exists as of M6, the architecture diagram, the Changed-event data flow, conventions, quality stack, and every open question in one place. |
| 01 | [Geometry](01-geometry.md) | The dependency-free curve layer — every edge is one cubic Bézier; evaluation, offsets, intersections, min radius. |
| 02 | [Network & validation](02-network-validation.md) | `RoadNetwork`, the source of truth: the six-type catalog and the `Validate`/`Commit` placement contract. |
| 03 | [Junctions & control](03-junctions-control.md) | Where asphalt stops (cut points, corner zones) and who yields (control modes + signal phases). |
| 04 | [Lane graph & connectors](04-lane-graph-connectors.md) | Turning lanes into the connector graph vehicles drive — turn-lane assignment and conflict points, the most bug-scarred file. |
| 05 | [Traffic simulation](05-traffic-sim.md) | The deterministic microsim: IDM following, MOBIL-lite lane changes, A* routing, the conflict-point junction arbiter. |
| 06 | [Drafting & snapping](06-drafting-snapping.md) | The gesture state machine and scored snap resolver that produce placement proposals. |
| 07 | [Rendering & markings](07-rendering-markings.md) | The only Godot-aware layer: the resync pattern, lane-profile meshes, junction paint, instanced vehicles. |
| 08 | [Persistence](08-persistence.md) | Versioned JSON save/load with a byte-stable round-trip contract. |
| — | [Glossary](glossary.md) | 60 cross-cutting terms, each linked to the chapter section that explains it best. |

## How the manual stays current

The manual is authored and maintained with the `explain-codebase` skill and is part of the
milestone workflow, not a one-off. Per [docs/verification.md](../verification.md), at the
end of each milestone the skill's update mode runs against what actually changed, detecting
drift and refreshing the affected chapters so prose keeps matching code. The obligation is
not to skip it. When you change a subsystem, update its chapter's "Last verified against
commit" stamp and any file:line anchors you moved — the chapter's *How to verify* section
tells you which harness proves the change.

The working notes, outline, and manifest that produced this manual live in
[`.workspace/`](.workspace/) and are kept for the next update pass; they are not part of the
published book.
