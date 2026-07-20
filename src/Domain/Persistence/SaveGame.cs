namespace CityBuilder.Domain.Persistence;

/// <summary>Raised for any structurally invalid or unsupported save payload: a
/// <see cref="SaveGame.FormatVersion"/> newer than <see cref="SaveLoad.FormatVersion"/>,
/// an id at or beyond its counter, a dangling edge/node reference, or a lane-id count
/// that doesn't match the catalog's road type.</summary>
public sealed class SaveFormatException(string message) : Exception(message);

// DTOs: plain ints for ids, arrays ordered by id for byte-stable output.
// Roundabouts/NextRoundabout are absent in format v1 saves (deserialize null/0); the
// loader treats null as empty and a 0 counter as 1.
public sealed record SaveGame(int FormatVersion, NodeDto[] Nodes, EdgeDto[] Edges,
    int NextNode, int NextEdge, int NextLane,
    RoundaboutDto[]? Roundabouts = null, int NextRoundabout = 0);

public sealed record NodeDto(int Id, float X, float Y, float Z, ConfigDto Config);

public sealed record ConfigDto(int Mode, float SizeOffset, RoleDto[] Roles, LegOffsetDto[] LegOffsets);

public sealed record RoleDto(int Edge, int Role);

public sealed record LegOffsetDto(int Edge, float Offset);

// v3 (M8.5) appends Covered; absent in v1/v2 payloads → default false.
public sealed record EdgeDto(int Id, int Start, int End, int Type,
    float[] Curve /* 12 floats: P0..P3, each X,Y,Z */, int[] LaneIds /* catalog order */,
    bool Covered = false);

/// <summary>A roundabout: identity, geometry, ring membership (CCW), and each approach
/// leg's full pre-conversion curve so radius edits re-trim losslessly after a reload.
/// Ring nodes/edges themselves persist as ordinary <see cref="NodeDto"/>/<see cref="EdgeDto"/>.</summary>
public sealed record RoundaboutDto(int Id, float CX, float CY, float CZ, float Radius,
    int[] RingNodeIds /* CCW */, int[] RingEdgeIds /* CCW */, LegCurveDto[] LegCurves);

public sealed record LegCurveDto(int Edge, float[] Curve /* 12 floats: P0..P3 */);
