using System.Numerics;
using CityBuilder.Domain.Catalog;

namespace CityBuilder.Domain.Network;

public enum JunctionControlMode { Auto, None, PrioritySigns, AllWayStop, TrafficLights }

/// <summary>Role of one approach at a priority-controlled junction.</summary>
public enum LegRole { Main, Yield, Stop }

/// <summary>Right-of-way class a lane connector carries into a junction; the traffic
/// simulation consumes this (Signal = controlled by lights).</summary>
public enum RightOfWay { Free, Yield, Stop, Signal }

/// <summary>Authored control state of a junction. Mode Auto resolves via heuristics;
/// role/size overrides are keyed by connected edge and pruned when topology changes.
/// SizeOffset/LegOffsets grow (or shrink, down to the geometric minimum) the junction
/// footprint in metres.</summary>
public sealed record JunctionConfig(
    JunctionControlMode Mode,
    IReadOnlyDictionary<EdgeId, LegRole> RoleOverrides,
    float SizeOffset,
    IReadOnlyDictionary<EdgeId, float> LegOffsets)
{
    public static readonly JunctionConfig Default = new(
        JunctionControlMode.Auto,
        new Dictionary<EdgeId, LegRole>(),
        0f,
        new Dictionary<EdgeId, float>());
}

/// <summary>Resolved control at a node: never Auto, one role per leg.</summary>
public sealed record EffectiveControl(
    JunctionControlMode Mode,
    IReadOnlyDictionary<EdgeId, LegRole> Roles);

public static class JunctionControl
{
    /// <summary>Resolve the effective control for a node. Bends and dead ends (degree
    /// ≤ 2) are always None; Auto at real junctions becomes priority signs with the
    /// widest (then straightest) pair of legs as the main road.</summary>
    public static EffectiveControl Resolve(RoadNode node, IReadOnlyDictionary<EdgeId, RoadEdge> edges)
    {
        var ids = node.Edges.OrderBy(e => e.Value).ToArray();
        if (ids.Length <= 2)
            return new EffectiveControl(JunctionControlMode.None,
                ids.ToDictionary(e => e, _ => LegRole.Main));

        var mode = node.Config.Mode == JunctionControlMode.Auto
            ? JunctionControlMode.PrioritySigns
            : node.Config.Mode;
        if (mode != JunctionControlMode.PrioritySigns)
            return new EffectiveControl(mode, ids.ToDictionary(e => e, _ => LegRole.Main));

        var (mainA, mainB) = MainPair(node, edges, ids);
        var roles = ids.ToDictionary(
            e => e,
            e => e == mainA || e == mainB ? LegRole.Main : LegRole.Yield);
        foreach (var (eid, role) in node.Config.RoleOverrides)
            if (roles.ContainsKey(eid))
                roles[eid] = role;
        return new EffectiveControl(mode, roles);
    }

    /// <summary>Widest carriageway pair wins; straightest continuation breaks ties.
    /// Deterministic: candidates are scanned in edge-id order.</summary>
    private static (EdgeId, EdgeId) MainPair(
        RoadNode node, IReadOnlyDictionary<EdgeId, RoadEdge> edges, EdgeId[] ids)
    {
        var best = (ids[0], ids[1]);
        (float width, float straight) bestScore = (float.MinValue, float.MinValue);
        for (int i = 0; i < ids.Length; i++)
        for (int j = i + 1; j < ids.Length; j++)
        {
            float w = RoadCatalog.Get(edges[ids[i]].Type).CarriagewayHalf
                    + RoadCatalog.Get(edges[ids[j]].Type).CarriagewayHalf;
            float s = -Vector2.Dot(LeavingDir(edges[ids[i]], node), LeavingDir(edges[ids[j]], node));
            if (w > bestScore.width + 0.01f
                || (MathF.Abs(w - bestScore.width) <= 0.01f && s > bestScore.straight))
            {
                bestScore = (w, s);
                best = (ids[i], ids[j]);
            }
        }
        return best;
    }

    private static Vector2 LeavingDir(RoadEdge edge, RoadNode node)
    {
        var t = edge.StartNode == node.Id ? edge.Curve.Tangent(0) : -edge.Curve.Tangent(1);
        var d = new Vector2(t.X, t.Z);
        return d.LengthSquared() > 0 ? Vector2.Normalize(d) : Vector2.UnitX;
    }
}
