using System.Numerics;
using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Traffic;

public enum SignalPhase { Green, Amber, Red }

/// <summary>Fixed-cycle two-phase signal for one lights-controlled junction. Legs are
/// clustered into two direction groups (opposing legs share a group); each group gets
/// green 12 s + amber 3 s, with 1 s all-red between. Deterministic under sim time.</summary>
public sealed class SignalController
{
    public const float GreenSec = 12f, AmberSec = 3f, AllRedSec = 1f;
    private const float HalfCycle = GreenSec + AmberSec + AllRedSec;

    private readonly Dictionary<EdgeId, int> _group = new();
    private float _t;

    public SignalController(RoadNode node, RoadNetwork network)
    {
        var legs = node.Edges.OrderBy(e => e.Value).ToArray();
        var dirs = legs.Select(e => LeavingDir(network.Edges[e], node)).ToArray();

        // best 2-partition: maximize alignment (|dot|) within groups
        int best = 1; // bit i set = leg i in group 1; leg 0 pinned to group 0
        float bestScore = float.MinValue;
        for (int mask = 0; mask < 1 << legs.Length; mask += 2)
        {
            float score = 0;
            bool hasZero = false, hasOne = false;
            for (int i = 0; i < legs.Length; i++)
            {
                if ((mask >> i & 1) == 0) hasZero = true; else hasOne = true;
                for (int j = i + 1; j < legs.Length; j++)
                    if ((mask >> i & 1) == (mask >> j & 1))
                        score += MathF.Abs(Vector2.Dot(dirs[i], dirs[j]));
            }
            if (legs.Length > 1 && (!hasZero || !hasOne))
                continue;
            if (score > bestScore)
            {
                bestScore = score;
                best = mask;
            }
        }
        for (int i = 0; i < legs.Length; i++)
            _group[legs[i]] = best >> i & 1;
    }

    public void Advance(float dt) => _t = (_t + dt) % (2 * HalfCycle);

    public SignalPhase Phase(EdgeId leg)
    {
        if (!_group.TryGetValue(leg, out var g))
            return SignalPhase.Red;
        float local = _t - g * HalfCycle;
        if (local < 0)
            local += 2 * HalfCycle;
        return local switch
        {
            < GreenSec => SignalPhase.Green,
            < GreenSec + AmberSec => SignalPhase.Amber,
            _ => SignalPhase.Red,
        };
    }

    private static Vector2 LeavingDir(RoadEdge edge, RoadNode node)
    {
        var t = edge.StartNode == node.Id ? edge.Curve.Tangent(0) : -edge.Curve.Tangent(1);
        var d = new Vector2(t.X, t.Z);
        return d.LengthSquared() > 0 ? Vector2.Normalize(d) : Vector2.UnitX;
    }
}
