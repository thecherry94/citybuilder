namespace CityBuilder.Domain.Persistence;

/// <summary>Raised for any structurally invalid or unsupported save payload: a
/// <see cref="SaveGame.FormatVersion"/> newer than <see cref="SaveLoad.FormatVersion"/>,
/// an id at or beyond its counter, a dangling edge/node reference, or a lane-id count
/// that doesn't match the catalog's road type.</summary>
public sealed class SaveFormatException(string message) : Exception(message);

// DTOs: plain ints for ids, arrays ordered by id for byte-stable output.
public sealed record SaveGame(int FormatVersion, NodeDto[] Nodes, EdgeDto[] Edges,
    int NextNode, int NextEdge, int NextLane);

public sealed record NodeDto(int Id, float X, float Y, float Z, ConfigDto Config);

public sealed record ConfigDto(int Mode, float SizeOffset, RoleDto[] Roles, LegOffsetDto[] LegOffsets);

public sealed record RoleDto(int Edge, int Role);

public sealed record LegOffsetDto(int Edge, float Offset);

public sealed record EdgeDto(int Id, int Start, int End, int Type,
    float[] Curve /* 12 floats: P0..P3, each X,Y,Z */, int[] LaneIds /* catalog order */);
