using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Catalog;

/// <summary>Paint rules from lane adjacency, valid for any lane profile — including
/// direction-asymmetric ones (the opposing boundary is wherever the innermost
/// opposing lanes meet, not offset 0). Pure domain so tests cover it headlessly.
/// Also used to continue markings across degree-2 corner junctions.</summary>
public static class MarkingRules
{
    public const float EdgeLineInset = 0.4f;

    public static IEnumerable<(float Offset, bool Dashed)> Layout(RoadType type)
    {
        var driving = type.Lanes.Where(l => l.Kind == LaneKind.Driving).OrderBy(l => l.Offset).ToArray();
        if (driving.Length == 0)
            yield break;

        for (int i = 0; i + 1 < driving.Length; i++)
        {
            float boundary = (driving[i].Offset + driving[i].Width / 2
                + driving[i + 1].Offset - driving[i + 1].Width / 2) / 2;
            if (driving[i].Direction == driving[i + 1].Direction)
                yield return (boundary, true);              // lane separator
            else if (driving.Length <= 2)
                yield return (boundary, true);              // small road: dashed center
            else
            {
                yield return (boundary - 0.18f, false);     // double solid center
                yield return (boundary + 0.18f, false);
            }
        }

        foreach (int side in new[] { -1, +1 })
        {
            var outermost = side < 0 ? driving[0] : driving[^1];
            float carriagewayEdge = outermost.Offset + side * outermost.Width / 2;
            var beyond = type.Lanes
                .Where(l => l.Kind != LaneKind.Driving
                    && MathF.Sign(l.Offset) == side
                    && MathF.Abs(l.Offset) > MathF.Abs(outermost.Offset))
                .OrderBy(l => MathF.Abs(l.Offset))
                .FirstOrDefault();

            if (beyond is null)
                yield return (side * (type.Width / 2 - EdgeLineInset), false); // rural edge line
            else if (beyond.Kind == LaneKind.Bicycle)
                yield return (carriagewayEdge, false);      // solid bike separation
            // sidewalk adjacent: the curb is the boundary, no paint
        }
    }
}
