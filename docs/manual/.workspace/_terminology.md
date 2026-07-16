# Terminology (seed — grown by chapter authors, compiled into glossary.md at Phase 6)

## Traffic frame
Project convention: XZ ground plane, +Y up; right-hand traffic. Lane offsets are signed perpendicular to the curve tangent. See docs/conventions.md.

## Stranded lane
An arriving driving lane with zero outgoing connectors. LEGAL iff its node offers no departing driving lane on another edge (CS2-style ruling, spec amendment ceb1887); routing never uses them. When departing capacity exists, ConnectorBuilder's never-strand fallback guarantees ≥1 connector.

## Connector
A bezier curve linking an arriving lane to a departing lane across a node; what vehicles actually drive through junctions. Carries TurnKind + RightOfWay.

## G1 tangent-continuation exemption
Validate allows a new edge to depart an existing mid-edge point within TangentContinuationDeg (1°) of the existing tangent — the "ramp exit" case exempt from the 25° junction-angle floor.
