using System.Text.Json;
using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Persistence;

/// <summary>Versioned JSON persistence for <see cref="RoadNetwork"/>. Serialization is
/// deterministic (sorted ids, no indentation, shared options) so
/// <c>Save(Load(Save(n)))</c> is byte-equal to <c>Save(n)</c> — a useful regression
/// check and the basis for diffable save files. Derived data (lane geometry, junction
/// polygons, connectors) is never stored; it rebuilds from the restored graph.</summary>
public static class SaveLoad
{
    public const int FormatVersion = 2;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
    };

    public static string Save(RoadNetwork n) => JsonSerializer.Serialize(ToSaveGame(n), Options);

    /// <summary>Deserialize into a fresh network.</summary>
    public static RoadNetwork Load(string json)
    {
        var n = new RoadNetwork();
        LoadInto(json, n);
        return n;
    }

    /// <summary>Clear and repopulate an existing network in place, inside one mutation
    /// batch — subscribers observe exactly one <see cref="RoadNetwork.Changed"/> event.</summary>
    public static void LoadInto(string json, RoadNetwork n)
    {
        SaveGame game;
        try
        {
            game = JsonSerializer.Deserialize<SaveGame>(json, Options)
                   ?? throw new SaveFormatException("save data is empty");
        }
        catch (JsonException ex)
        {
            throw new SaveFormatException($"malformed save JSON: {ex.Message}");
        }

        if (game.FormatVersion > FormatVersion)
            throw new SaveFormatException(
                $"save format {game.FormatVersion} is newer than the supported format {FormatVersion}");

        n.RestoreInto(game);
    }

    private static SaveGame ToSaveGame(RoadNetwork n)
    {
        var nodes = n.Nodes.Values
            .OrderBy(x => x.Id.Value)
            .Select(ToNodeDto)
            .ToArray();
        var edges = n.Edges.Values
            .OrderBy(x => x.Id.Value)
            .Select(ToEdgeDto)
            .ToArray();
        var roundabouts = n.Roundabouts.Values
            .OrderBy(x => x.Id.Value)
            .Select(ToRoundaboutDto)
            .ToArray();
        return new SaveGame(FormatVersion, nodes, edges,
            n.NextNodeCounter, n.NextEdgeCounter, n.NextLaneCounter,
            roundabouts, n.NextRoundaboutCounter);
    }

    private static RoundaboutDto ToRoundaboutDto(Roundabout r) => new(
        r.Id.Value, r.Center.X, r.Center.Y, r.Center.Z, r.Radius,
        r.RingNodes.Select(x => x.Value).ToArray(),
        r.RingEdges.Select(x => x.Value).ToArray(),
        r.LegFullCurves.OrderBy(kv => kv.Key.Value)
            .Select(kv => new LegCurveDto(kv.Key.Value, CurveFloats(kv.Value)))
            .ToArray());

    private static float[] CurveFloats(in CityBuilder.Domain.Geometry.Bezier3 c) => new[]
    {
        c.P0.X, c.P0.Y, c.P0.Z, c.P1.X, c.P1.Y, c.P1.Z,
        c.P2.X, c.P2.Y, c.P2.Z, c.P3.X, c.P3.Y, c.P3.Z,
    };

    private static NodeDto ToNodeDto(RoadNode node) => new(
        node.Id.Value, node.Position.X, node.Position.Y, node.Position.Z, ToConfigDto(node.Config));

    private static ConfigDto ToConfigDto(JunctionConfig c) => new(
        (int)c.Mode,
        c.SizeOffset,
        c.RoleOverrides.OrderBy(kv => kv.Key.Value)
            .Select(kv => new RoleDto(kv.Key.Value, (int)kv.Value))
            .ToArray(),
        c.LegOffsets.OrderBy(kv => kv.Key.Value)
            .Select(kv => new LegOffsetDto(kv.Key.Value, kv.Value))
            .ToArray());

    private static EdgeDto ToEdgeDto(RoadEdge edge)
    {
        var c = edge.Curve;
        return new EdgeDto(
            edge.Id.Value, edge.StartNode.Value, edge.EndNode.Value, edge.Type.Value,
            new[]
            {
                c.P0.X, c.P0.Y, c.P0.Z,
                c.P1.X, c.P1.Y, c.P1.Z,
                c.P2.X, c.P2.Y, c.P2.Z,
                c.P3.X, c.P3.Y, c.P3.Z,
            },
            edge.Lanes.Select(l => l.Id.Value).ToArray());
    }
}
