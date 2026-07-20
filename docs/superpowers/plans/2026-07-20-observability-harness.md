# M8.75 Observability Harness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Agent-readable geometry exports (SVG + JSON of the domain road network) wired into tests, fuzzer failures, and the smoke harness; plus golden-image diffing in the screenshot harness.

**Architecture:** A pure-domain static exporter `GeometryDump` (new `src/Domain/Diagnostics/`) reads only cached derived data off `RoadNetwork` (no rebuilds, no mutations). Consumers: xUnit tests directly, `FuzzSuiteTests` on invariant failure (via a small `FuzzArtifacts` helper), `Main.RunSmoke` behind `CITYBUILDER_SMOKE_DUMP=<dir>`. Golden diffing lives inside `VisualShots` (in-harness byte comparison via `Godot.Image` — **not** ImageMagick: no PATH dependency inside the Godot process, and per-shot logic stays in one file; spec is amended accordingly in Task 7).

**Tech Stack:** .NET 8 domain (System.Numerics, System.Text.Json), xUnit (net10.0 tests), Godot 4.7 mono game layer.

## Global Constraints

- `src/Domain` never references Godot (golden rule 1). `GeometryDump` is domain-pure.
- All SVG number formatting MUST use `CultureInfo.InvariantCulture` (dev machine locale is de-DE; `3,5` instead of `3.5` corrupts SVG). JSON via System.Text.Json is invariant by construction.
- Plan-view mapping: SVG x = world X (metres), SVG y = world Z (metres), stated in an SVG header comment. Elevation (world Y) appears only in labels.
- Golden thresholds: per-channel tolerance 8/255, max changed-pixel fraction 0.5 % per shot.
- Golden baselines live in `tests/visual/golden/` (tracked; `tests/visual/shots/` stays gitignored).
- Every task ends green: `dotnet test --filter "FullyQualifiedName!~Fuzz"` (quick gate ≈ 2 s) + `dotnet build citybuilder.sln`; harness tasks additionally run their harness. Commit per green task.
- `godot` = `%LOCALAPPDATA%\Programs\Godot-4.7-mono\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe` if not on PATH.

---

### Task 1: `GeometryDump.Json`

**Files:**
- Create: `src/Domain/Diagnostics/GeometryDump.cs`
- Test: `tests/Domain.Tests/Diagnostics/GeometryDumpJsonTests.cs`

**Interfaces:**
- Consumes: `RoadNetwork.Nodes/Edges/Roundabouts` (`src/Domain/Network/RoadNetwork.cs:39-41`), `RoadEdge.Curve/Type/Covered/Lanes` (`src/Domain/Network/Entities.cs:33-80`), `RoadCatalog.Get` (`src/Domain/Catalog/RoadType.cs:135`), `BezierOps.Tessellate` (`src/Domain/Geometry/BezierOps.cs:13`).
- Produces: `namespace CityBuilder.Domain.Diagnostics`, `public static class GeometryDump` with `public static string Json(RoadNetwork network)` and `public static void JsonToFile(RoadNetwork network, string path)`. Tasks 2–4 add/consume `Svg`/`SvgToFile` on the same class.

- [ ] **Step 1: Write the failing test**

`tests/Domain.Tests/Diagnostics/GeometryDumpJsonTests.cs`:

```csharp
using System.Numerics;
using System.Text.Json;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Diagnostics;
using CityBuilder.Domain.Tests.Network;

namespace CityBuilder.Domain.Tests.Diagnostics;

public class GeometryDumpJsonTests
{
    [Fact]
    public void JsonRoundTripsCountsAndCoordinates()
    {
        var n = Net.New();
        // 4-way cross + a detached climbing edge (10 % on a 60 m run, under every cap)
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100)));
        Net.Commit(n, Net.Straight(new(200, 0, 0), new(260, 6, 0)));

        using var doc = JsonDocument.Parse(GeometryDump.Json(n));
        var root = doc.RootElement;

        Assert.Equal(n.Nodes.Count, root.GetProperty("nodes").GetArrayLength());
        Assert.Equal(n.Edges.Count, root.GetProperty("edges").GetArrayLength());
        Assert.Equal(0, root.GetProperty("roundabouts").GetArrayLength());
        Assert.Equal(n.Version, root.GetProperty("version").GetInt32());

        // every edge polyline starts at P0 and ends at P3 (2-decimal rounding)
        foreach (var e in root.GetProperty("edges").EnumerateArray())
        {
            var id = e.GetProperty("id").GetInt32();
            var curve = n.Edges[new(id)].Curve;
            var poly = e.GetProperty("polyline");
            AssertPoint(curve.P0, poly[0]);
            AssertPoint(curve.P3, poly[poly.GetArrayLength() - 1]);
            Assert.True(e.GetProperty("lanes").GetArrayLength() > 0);
            Assert.False(e.GetProperty("covered").GetBoolean());
        }

        // node positions survive
        foreach (var nd in root.GetProperty("nodes").EnumerateArray())
        {
            var id = nd.GetProperty("id").GetInt32();
            AssertPoint(n.Nodes[new(id)].Position, nd.GetProperty("position"));
        }
    }

    [Fact]
    public void JsonListsRoundaboutMembership()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-80, 0, 0), new(80, 0, 0)));
        Net.Commit(n, Net.Straight(new(0, 0, -80), new(0, 0, 80)));
        var center = n.Nodes.Values.Single(nd => nd.Position.Length() < 1f);
        var res = n.ConvertToRoundabout(center.Id, 16f);
        Assert.True(res.Success, res.Error?.ToString());

        using var doc = JsonDocument.Parse(GeometryDump.Json(n));
        var rb = Assert.Single(doc.RootElement.GetProperty("roundabouts").EnumerateArray());
        Assert.Equal(16f, rb.GetProperty("radius").GetSingle(), 2);
        Assert.True(rb.GetProperty("ringEdges").GetArrayLength() >= 4);
    }

    private static void AssertPoint(Vector3 expected, JsonElement actual)
    {
        Assert.Equal(expected.X, actual[0].GetSingle(), 1);
        Assert.Equal(expected.Y, actual[1].GetSingle(), 1);
        Assert.Equal(expected.Z, actual[2].GetSingle(), 1);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~GeometryDumpJsonTests"`
Expected: FAIL — compile error, `CityBuilder.Domain.Diagnostics` does not exist.

- [ ] **Step 3: Write minimal implementation**

`src/Domain/Diagnostics/GeometryDump.cs`:

```csharp
using System.Text.Json;
using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Diagnostics;

/// <summary>
/// Agent-readable exports of the domain road network: JSON for exact-coordinate
/// queries, SVG (Task 2) for at-a-glance plan-view reading. Reads only cached
/// derived data — never mutates or rebuilds.
/// </summary>
public static class GeometryDump
{
    private const float ChordTolerance = 0.25f;

    public static string Json(RoadNetwork network)
    {
        var dto = new
        {
            version = network.Version,
            nodes = network.Nodes.Values.OrderBy(n => n.Id.Value).Select(n => new
            {
                id = n.Id.Value,
                position = Pt(n.Position),
                edges = n.Edges.Select(e => e.Value).OrderBy(v => v).ToArray(),
                ring = n.Ring?.Value,
                junctionPolygon = n.Junction.SurfacePolygon.Select(Pt).ToArray(),
            }).ToArray(),
            edges = network.Edges.Values.OrderBy(e => e.Id.Value).Select(e =>
            {
                var type = RoadCatalog.Get(e.Type);
                return new
                {
                    id = e.Id.Value,
                    type = type.Name,
                    width = type.Width,
                    covered = e.Covered,
                    start = e.StartNode.Value,
                    end = e.EndNode.Value,
                    polyline = Polyline(e.Curve),
                    lanes = e.Lanes.Select(l => new
                    {
                        id = l.Id.Value,
                        offset = l.Offset,
                        direction = l.Direction.ToString(),
                        kind = l.Kind.ToString(),
                        width = l.Width,
                    }).ToArray(),
                };
            }).ToArray(),
            roundabouts = network.Roundabouts.Values.OrderBy(r => r.Id.Value).Select(r => new
            {
                id = r.Id.Value,
                center = Pt(r.Center),
                radius = r.Radius,
                ringNodes = r.RingNodes.Select(n => n.Value).ToArray(),
                ringEdges = r.RingEdges.Select(e => e.Value).ToArray(),
            }).ToArray(),
        };
        return JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
    }

    public static void JsonToFile(RoadNetwork network, string path) =>
        File.WriteAllText(path, Json(network));

    private static float[] Pt(System.Numerics.Vector3 v) =>
        new[] { MathF.Round(v.X, 2), MathF.Round(v.Y, 2), MathF.Round(v.Z, 2) };

    private static float[][] Polyline(in Bezier3 curve)
    {
        var c = curve;
        return BezierOps.Tessellate(c, ChordTolerance).Select(t => Pt(c.Point(t))).ToArray();
    }
}
```

Note: `Tessellate` takes `in Bezier3` — the local copy `c` avoids capturing an `in` parameter in the LINQ lambda. If `Bezier3` is passed by value everywhere anyway, taking `Bezier3 curve` (no `in`) is fine too.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~GeometryDumpJsonTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Quick gate + build**

Run: `dotnet test --filter "FullyQualifiedName!~Fuzz"` then `dotnet build citybuilder.sln`
Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add src/Domain/Diagnostics/GeometryDump.cs tests/Domain.Tests/Diagnostics/GeometryDumpJsonTests.cs
git commit -m "feat(m8.75): GeometryDump.Json — agent-readable network export"
```

---

### Task 2: `GeometryDump.Svg`

**Files:**
- Modify: `src/Domain/Diagnostics/GeometryDump.cs`
- Test: `tests/Domain.Tests/Diagnostics/GeometryDumpSvgTests.cs`

**Interfaces:**
- Consumes: everything Task 1 consumes, plus `RoadNode.Junction.SurfacePolygon` / `Connectors` / `ConnectorConflicts` (`src/Domain/Network/Entities.cs:18-24`), `LaneConnector.Curve` (`Entities.cs:143`), `ConflictPoint(int Other, float SMine, float STheirs)` (`src/Domain/Network/ConnectorBuilder.cs:9`), `Bezier3.OffsetPoint/Length` (`src/Domain/Geometry/Bezier3.cs:65,80`), `RoadType.OuterHalf` (`src/Domain/Catalog/RoadType.cs:43`).
- Produces: `public static string Svg(RoadNetwork network)` and `public static void SvgToFile(RoadNetwork network, string path)` on `GeometryDump`.

**SVG layout contract** (what tests assert and future readers rely on):
- Header comment: `<!-- plan view: svg x = world X (m), svg y = world Z (m); elevation (world Y) appears in labels -->`
- `viewBox` = network bounds padded 15 m, min 60×60 m for tiny networks. White background rect.
- Five layer groups, in paint order: `<g id="edges">` (carriageway bands: centerline path with `stroke-width` = type width, light gray `#d8d8d8`, covered edges get `stroke-dasharray="4 2"` and `#b0c4de`; plus a 0.3-wide dark centerline), `<g id="junctions">` (surface polygons `#c8c8c8` stroke `#666` stroke-width 0.3; roundabout ring circles stroke `#8a2be2` fill none), `<g id="lanes">` (per-lane polylines sampled at `OffsetPoint(t, lane.Offset)`, listed in **travel order** — reversed for `LaneDirection.Backward` — with `marker-end` arrowheads; stroke by kind: Driving `#2b6cb0`, Sidewalk `#718096`, Bicycle `#2f855a`; width 0.25; connector curves dashed `#9f7aea` width 0.2), `<g id="conflicts">` (red `#e53e3e` circles r=0.5 at each conflict point, deduped to `Other > index` pairs, positioned at `connector.Curve.Point(min(1, SMine / connector.Curve.Length()))`), `<g id="labels">` (per node: `n{id}` at position, font-size 2.5, `#333`; per edge at `Curve.Point(0.5)`: `e{id} {typeName}` plus ` y={±0.#}` when `|midpoint.Y| > 0.05` plus ` covered` when covered; per roundabout: `rb{id} r={radius}` at center).
- All numbers written via a single helper `F(float)` using `CultureInfo.InvariantCulture`, format `0.##`.

- [ ] **Step 1: Write the failing test**

`tests/Domain.Tests/Diagnostics/GeometryDumpSvgTests.cs`:

```csharp
using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using CityBuilder.Domain.Diagnostics;
using CityBuilder.Domain.Tests.Network;

namespace CityBuilder.Domain.Tests.Diagnostics;

public class GeometryDumpSvgTests
{
    private static CityBuilder.Domain.Network.RoadNetwork CrossWithBridge()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(-100, 0, 0), new(100, 0, 0)));
        Net.Commit(n, Net.Straight(new(0, 0, -100), new(0, 0, 100)));
        Net.Commit(n, Net.Straight(new(200, 0, 0), new(260, 6, 0)));
        return n;
    }

    [Fact]
    public void SvgIsWellFormedWithAllLayers()
    {
        var svg = GeometryDump.Svg(CrossWithBridge());
        var doc = XDocument.Parse(svg); // throws on malformed XML
        var ns = doc.Root!.Name.Namespace;
        var groupIds = doc.Root.Elements(ns + "g").Select(g => (string?)g.Attribute("id")).ToList();
        Assert.Equal(new[] { "edges", "junctions", "lanes", "conflicts", "labels" }, groupIds);
        Assert.Contains("svg x = world X", svg);
    }

    [Fact]
    public void SvgLabelsElevationAndIds()
    {
        var svg = GeometryDump.Svg(CrossWithBridge());
        Assert.Contains("y=3", svg);      // climbing edge midpoint sits at Y=3
        Assert.Contains(">n0<", svg);     // first node label text
        Assert.Contains("e0 TwoLane", svg);
    }

    [Fact]
    public void SvgConflictLayerPopulatedAtAJunction()
    {
        var svg = GeometryDump.Svg(CrossWithBridge());
        var doc = XDocument.Parse(svg);
        var ns = doc.Root!.Name.Namespace;
        var conflicts = doc.Root.Elements(ns + "g").Single(g => (string?)g.Attribute("id") == "conflicts");
        Assert.True(conflicts.Elements(ns + "circle").Count() > 0, "4-way cross must have conflict points");
    }

    [Fact]
    public void SvgIsCultureInvariant()
    {
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var svg = GeometryDump.Svg(CrossWithBridge());
            var doc = XDocument.Parse(svg);
            var viewBox = ((string)doc.Root!.Attribute("viewBox")!).Split(' ');
            Assert.Equal(4, viewBox.Length);
            foreach (var part in viewBox)
                Assert.True(float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
                    $"viewBox token '{part}' is not invariant-culture parseable");
        }
        finally { CultureInfo.CurrentCulture = prev; }
    }
}
```

Note on `>n0<` / `e0 TwoLane`: ids start at 0 for the first committed edge/node — verify against the actual first-assigned ids while implementing; if ids start at 1, adjust the two literals (check `n.Nodes.Keys.Min()`), keeping the assertions exact.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~GeometryDumpSvgTests"`
Expected: FAIL — `GeometryDump.Svg` not defined.

- [ ] **Step 3: Implement `Svg`**

Append to `GeometryDump` (same file; `using System.Globalization;`, `using System.Text;` added at top):

```csharp
    public static string Svg(RoadNetwork network)
    {
        // bounds over node positions + edge control points, padded
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        void Grow(System.Numerics.Vector3 v)
        {
            minX = MathF.Min(minX, v.X); maxX = MathF.Max(maxX, v.X);
            minZ = MathF.Min(minZ, v.Z); maxZ = MathF.Max(maxZ, v.Z);
        }
        foreach (var n in network.Nodes.Values) Grow(n.Position);
        foreach (var e in network.Edges.Values) { Grow(e.Curve.P0); Grow(e.Curve.P1); Grow(e.Curve.P2); Grow(e.Curve.P3); }
        if (network.Nodes.Count == 0) { minX = minZ = -30; maxX = maxZ = 30; }
        const float pad = 15f;
        minX -= pad; minZ -= pad; maxX += pad; maxZ += pad;
        if (maxX - minX < 60) { var c = (minX + maxX) / 2; minX = c - 30; maxX = c + 30; }
        if (maxZ - minZ < 60) { var c = (minZ + maxZ) / 2; minZ = c - 30; maxZ = c + 30; }

        var sb = new StringBuilder();
        sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{F(minX)} {F(minZ)} {F(maxX - minX)} {F(maxZ - minZ)}\">");
        sb.AppendLine("<!-- plan view: svg x = world X (m), svg y = world Z (m); elevation (world Y) appears in labels -->");
        sb.AppendLine("<defs><marker id=\"arrow\" viewBox=\"0 0 4 4\" refX=\"3\" refY=\"2\" markerWidth=\"4\" markerHeight=\"4\" orient=\"auto\"><path d=\"M0,0 L4,2 L0,4 z\"/></marker></defs>");
        sb.AppendLine($"<rect x=\"{F(minX)}\" y=\"{F(minZ)}\" width=\"{F(maxX - minX)}\" height=\"{F(maxZ - minZ)}\" fill=\"white\"/>");

        // ---- edges
        sb.AppendLine("<g id=\"edges\">");
        foreach (var e in network.Edges.Values.OrderBy(e => e.Id.Value))
        {
            var type = RoadCatalog.Get(e.Type);
            var d = PathD(e.Curve);
            var band = e.Covered
                ? $"stroke=\"#b0c4de\" stroke-dasharray=\"4 2\""
                : "stroke=\"#d8d8d8\"";
            sb.AppendLine($"<path d=\"{d}\" fill=\"none\" {band} stroke-width=\"{F(type.Width)}\"/>");
            sb.AppendLine($"<path d=\"{d}\" fill=\"none\" stroke=\"#555\" stroke-width=\"0.3\"/>");
        }
        sb.AppendLine("</g>");

        // ---- junctions
        sb.AppendLine("<g id=\"junctions\">");
        foreach (var n in network.Nodes.Values.OrderBy(n => n.Id.Value))
        {
            var poly = n.Junction.SurfacePolygon;
            if (poly.Count >= 3)
            {
                var pts = string.Join(" ", poly.Select(p => $"{F(p.X)},{F(p.Z)}"));
                sb.AppendLine($"<polygon points=\"{pts}\" fill=\"#c8c8c8\" stroke=\"#666\" stroke-width=\"0.3\"/>");
            }
        }
        foreach (var r in network.Roundabouts.Values.OrderBy(r => r.Id.Value))
            sb.AppendLine($"<circle cx=\"{F(r.Center.X)}\" cy=\"{F(r.Center.Z)}\" r=\"{F(r.Radius)}\" fill=\"none\" stroke=\"#8a2be2\" stroke-width=\"0.4\"/>");
        sb.AppendLine("</g>");

        // ---- lanes (travel order + arrowheads) and node connectors
        sb.AppendLine("<g id=\"lanes\">");
        foreach (var e in network.Edges.Values.OrderBy(e => e.Id.Value))
        {
            var c = e.Curve;
            var ts = BezierOps.Tessellate(c, ChordTolerance);
            foreach (var lane in e.Lanes)
            {
                var pts = ts.Select(t => c.OffsetPoint(t, lane.Offset)).ToList();
                if (lane.Direction == LaneDirection.Backward) pts.Reverse();
                var poly = string.Join(" ", pts.Select(p => $"{F(p.X)},{F(p.Z)}"));
                var color = lane.Kind switch
                {
                    LaneKind.Driving => "#2b6cb0",
                    LaneKind.Bicycle => "#2f855a",
                    _ => "#718096",
                };
                sb.AppendLine($"<polyline points=\"{poly}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"0.25\" marker-end=\"url(#arrow)\"/>");
            }
        }
        foreach (var n in network.Nodes.Values.OrderBy(n => n.Id.Value))
            foreach (var conn in n.Connectors)
                sb.AppendLine($"<path d=\"{PathD(conn.Curve)}\" fill=\"none\" stroke=\"#9f7aea\" stroke-width=\"0.2\" stroke-dasharray=\"1 0.7\"/>");
        sb.AppendLine("</g>");

        // ---- conflict points (dedupe: only pairs with Other > my index)
        sb.AppendLine("<g id=\"conflicts\">");
        foreach (var n in network.Nodes.Values.OrderBy(n => n.Id.Value))
        {
            for (int ci = 0; ci < n.ConnectorConflicts.Count; ci++)
            {
                var curve = n.Connectors[ci].Curve;
                var len = MathF.Max(curve.Length(), 0.01f);
                foreach (var cp in n.ConnectorConflicts[ci])
                {
                    if (cp.Other <= ci) continue;
                    var p = curve.Point(MathF.Min(1f, cp.SMine / len));
                    sb.AppendLine($"<circle cx=\"{F(p.X)}\" cy=\"{F(p.Z)}\" r=\"0.5\" fill=\"#e53e3e\"/>");
                }
            }
        }
        sb.AppendLine("</g>");

        // ---- labels
        sb.AppendLine("<g id=\"labels\" font-size=\"2.5\" fill=\"#333\" font-family=\"monospace\">");
        foreach (var n in network.Nodes.Values.OrderBy(n => n.Id.Value))
            sb.AppendLine($"<text x=\"{F(n.Position.X + 1)}\" y=\"{F(n.Position.Z - 1)}\">n{n.Id.Value}</text>");
        foreach (var e in network.Edges.Values.OrderBy(e => e.Id.Value))
        {
            var mid = e.Curve.Point(0.5f);
            var type = RoadCatalog.Get(e.Type);
            var label = $"e{e.Id.Value} {type.Name}";
            if (MathF.Abs(mid.Y) > 0.05f) label += $" y={F(mid.Y)}";
            if (e.Covered) label += " covered";
            sb.AppendLine($"<text x=\"{F(mid.X + 1)}\" y=\"{F(mid.Z + 3)}\">{label}</text>");
        }
        foreach (var r in network.Roundabouts.Values.OrderBy(r => r.Id.Value))
            sb.AppendLine($"<text x=\"{F(r.Center.X)}\" y=\"{F(r.Center.Z)}\">rb{r.Id.Value} r={F(r.Radius)}</text>");
        sb.AppendLine("</g>");

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    public static void SvgToFile(RoadNetwork network, string path) =>
        File.WriteAllText(path, Svg(network));

    private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string PathD(in Bezier3 c) =>
        $"M {F(c.P0.X)} {F(c.P0.Z)} C {F(c.P1.X)} {F(c.P1.Z)}, {F(c.P2.X)} {F(c.P2.Z)}, {F(c.P3.X)} {F(c.P3.Z)}";
```

Implementation notes:
- `RoadType.Name` values come from `RoadCatalog` (`TwoLane` etc.) — the test's `"e0 TwoLane"` literal must match the catalog's actual `Name` string; check `RoadCatalog.TwoLane.Name` while implementing.
- Cubic paths project control points to XZ — exact for plan view (projection of a 3D cubic is the 2D cubic of projected control points).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~GeometryDumpSvgTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Eyeball one real dump**

Write a throwaway scratchpad script or temporary test writing `GeometryDump.SvgToFile` for the cross+bridge network to the scratchpad dir; open/read the SVG to confirm it visually reads as a plan view (Claude reads the SVG source + renders mentally; optionally convert with magick to PNG). Delete any temporary test afterwards.

- [ ] **Step 6: Quick gate + build + commit**

Run: `dotnet test --filter "FullyQualifiedName!~Fuzz"` then `dotnet build citybuilder.sln`
Expected: green.

```bash
git add src/Domain/Diagnostics/GeometryDump.cs tests/Domain.Tests/Diagnostics/GeometryDumpSvgTests.cs
git commit -m "feat(m8.75): GeometryDump.Svg — layered plan-view export"
```

---

### Task 3: Fuzzer failure dumps (`FuzzArtifacts`)

**Files:**
- Create: `tests/Domain.Tests/Fuzzing/FuzzArtifacts.cs`
- Modify: `tests/Domain.Tests/Fuzzing/FuzzSuiteTests.cs:28-31` (the assert block)
- Test: `tests/Domain.Tests/Fuzzing/FuzzArtifactsTests.cs`

**Interfaces:**
- Consumes: `GeometryDump.SvgToFile/JsonToFile` (Tasks 1–2), `GestureFuzzer.LastNetwork` (`tests/Domain.Tests/Fuzzing/GestureFuzzer.cs:59`), `FuzzResult { Ok, FailedAtAction, Failure, ActionTail }` (`GestureFuzzer.cs:12-18`).
- Produces: `internal static class FuzzArtifacts` with `internal static string DumpOnFailure(RoadNetwork? network, string tag)` → returns `""` when network is null, else writes `fuzz-artifacts/<tag>.svg|.json` under `Directory.GetCurrentDirectory()` and returns a `\ngeometry dumps: <abs path>.svg|.json` suffix for the assert message.

- [ ] **Step 1: Write the failing test**

`tests/Domain.Tests/Fuzzing/FuzzArtifactsTests.cs`:

```csharp
using CityBuilder.Domain.Tests.Network;

namespace CityBuilder.Domain.Tests.Fuzzing;

public class FuzzArtifactsTests
{
    [Fact]
    public void WritesSvgAndJsonAndReturnsPathSuffix()
    {
        var n = Net.New();
        Net.Commit(n, Net.Straight(new(0, 0, 0), new(100, 0, 0)));

        var msg = FuzzArtifacts.DumpOnFailure(n, "unit_test_probe");

        Assert.Contains("geometry dumps:", msg);
        var baseName = Path.Combine(Directory.GetCurrentDirectory(), "fuzz-artifacts", "unit_test_probe");
        Assert.True(File.Exists(baseName + ".svg"));
        Assert.True(File.Exists(baseName + ".json"));
        File.Delete(baseName + ".svg");
        File.Delete(baseName + ".json");
    }

    [Fact]
    public void NullNetworkIsANoOp()
    {
        Assert.Equal("", FuzzArtifacts.DumpOnFailure(null, "never_written"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~FuzzArtifactsTests"`
Expected: FAIL — `FuzzArtifacts` not defined. (Filter contains "Fuzz" but these are new fast tests; the slow suite is `FuzzSuiteTests`, not matched by this filter.)

- [ ] **Step 3: Implement**

`tests/Domain.Tests/Fuzzing/FuzzArtifacts.cs`:

```csharp
using CityBuilder.Domain.Diagnostics;
using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Tests.Fuzzing;

/// <summary>
/// On a fuzz failure, drop SVG+JSON geometry dumps next to the test binaries so the
/// failing seed ships with a picture, not just an action tail.
/// </summary>
internal static class FuzzArtifacts
{
    internal static string DumpOnFailure(RoadNetwork? network, string tag)
    {
        if (network is null)
            return "";
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "fuzz-artifacts");
        Directory.CreateDirectory(dir);
        var baseName = Path.Combine(dir, tag);
        GeometryDump.SvgToFile(network, baseName + ".svg");
        GeometryDump.JsonToFile(network, baseName + ".json");
        return $"\ngeometry dumps: {baseName}.svg|.json";
    }
}
```

Then wire it into `FuzzSuiteTests.cs` — replace the existing assert block:

```csharp
var result = GestureFuzzer.Run(new FuzzOptions(seed, actions));
var artifacts = result.Ok ? "" :
    FuzzArtifacts.DumpOnFailure(GestureFuzzer.LastNetwork, $"seed{seed}_action{result.FailedAtAction}");
Assert.True(result.Ok,
    $"seed {seed} failed at action {result.FailedAtAction}: {result.Failure}\n" +
    string.Join("\n", result.ActionTail) + artifacts);
```

(Match the actual current text of the assert when editing; check `GestureFuzzer.LastNetwork`'s exact declaration at `GestureFuzzer.cs:59` — if it's a different name/type, adapt the call, not the helper.)

- [ ] **Step 4: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~FuzzArtifactsTests"` → PASS.
Run: `dotnet test --filter "FullyQualifiedName~FuzzSuiteTests"` (default 300 actions × 3 seeds, fast since M8.5) → PASS (proves the wiring compiles and the green path is unchanged).

- [ ] **Step 5: Quick gate + build + commit**

```bash
git add tests/Domain.Tests/Fuzzing/FuzzArtifacts.cs tests/Domain.Tests/Fuzzing/FuzzArtifactsTests.cs tests/Domain.Tests/Fuzzing/FuzzSuiteTests.cs
git commit -m "feat(m8.75): fuzz failures auto-dump SVG+JSON geometry artifacts"
```

---

### Task 4: Smoke-harness dump (`CITYBUILDER_SMOKE_DUMP`)

**Files:**
- Modify: `src/Game/Main.cs` — inside `RunSmoke()`, immediately before `GD.Print("SMOKE OK")` (`Main.cs:780`).

**Interfaces:**
- Consumes: `GeometryDump.SvgToFile/JsonToFile`, `Main._network` (`Main.cs:16`).
- Produces: env contract — when `CITYBUILDER_SMOKE_DUMP=<dir>` is set alongside `CITYBUILDER_SMOKE=1`, the run writes `<dir>/smoke_network.svg` and `.json` and prints `SMOKE DUMP <svg path>`.

- [ ] **Step 1: Implement (no domain test — this is game-layer glue verified by running the harness)**

Insert before the `SMOKE OK` print in `RunSmoke()`:

```csharp
var dumpDir = OS.GetEnvironment("CITYBUILDER_SMOKE_DUMP");
if (!string.IsNullOrEmpty(dumpDir))
{
    System.IO.Directory.CreateDirectory(dumpDir);
    var svgPath = System.IO.Path.Combine(dumpDir, "smoke_network.svg");
    CityBuilder.Domain.Diagnostics.GeometryDump.SvgToFile(_network, svgPath);
    CityBuilder.Domain.Diagnostics.GeometryDump.JsonToFile(
        _network, System.IO.Path.Combine(dumpDir, "smoke_network.json"));
    GD.Print($"SMOKE DUMP {svgPath}");
}
```

(Use fully-qualified names or add a `using`; `Main.cs` already fully-qualifies domain traffic types in places — follow whichever style the file uses.)

- [ ] **Step 2: Build**

Run: `dotnet build citybuilder.sln`
Expected: green.

- [ ] **Step 3: Run smoke with the dump enabled and READ the SVG**

PowerShell:
```powershell
$env:CITYBUILDER_SMOKE = "1"
$env:CITYBUILDER_SMOKE_DUMP = "$env:TEMP\citybuilder-smoke-dump"
& "$env:LOCALAPPDATA\Programs\Godot-4.7-mono\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe" --headless --path C:\Projects\Godot\citybuilder
```
Expected: output contains `SMOKE DUMP …smoke_network.svg` then `SMOKE OK`, exit 0. Then **read the SVG** (it is the end-state of the whole smoke scenario: grid, roundabout, bridge, tunnel) and confirm it contains the five layers, roundabout circle, `covered` and `y=` labels — this is the milestone's real deliverable proving itself. Unset the env vars afterwards.

- [ ] **Step 4: Verify plain smoke still passes without the env var** (no dump line, `SMOKE OK`).

- [ ] **Step 5: Commit**

```bash
git add src/Game/Main.cs
git commit -m "feat(m8.75): CITYBUILDER_SMOKE_DUMP writes end-state geometry SVG+JSON"
```

---

### Task 5: Roundabout gallery scenario

**Files:**
- Modify: `src/Game/VisualShots.cs` — add one scenario to `Scenarios()` (after `"cross_4lane"`, `VisualShots.cs:249-253`).

**Interfaces:**
- Consumes: local helpers `Straight`/`Commit` (`VisualShots.cs:895-914`), `RoadNetwork.ConvertToRoundabout(NodeId center, float radius)` (`src/Domain/Network/RoadNetwork.Roundabouts.cs:273`).
- Produces: gallery shots `roundabout_top.png` / `roundabout_oblique.png`; scenario name `"roundabout"` used by Task 6's golden set.

- [ ] **Step 1: Add the scenario**

```csharp
yield return new Scenario("roundabout", n =>
{
    Commit(n, Straight(new(-80, 0, 0), new(80, 0, 0)));
    Commit(n, Straight(new(0, 0, -80), new(0, 0, 80)));
    var center = n.Nodes.Values.Single(nd => nd.Position.Length() < 1f);
    var res = n.ConvertToRoundabout(center.Id, 16f);
    if (!res.Success)
        throw new InvalidOperationException($"roundabout convert failed: {res.Error}");
}, Standard(new(0, 0, 0), 80));
```

- [ ] **Step 2: Build + run the screenshot harness**

Run: `dotnet build citybuilder.sln`, then PowerShell:
```powershell
$env:CITYBUILDER_SHOTS = "tests/visual/shots"
& "$env:LOCALAPPDATA\...\Godot_v4.7-stable_mono_win64_console.exe" --path C:\Projects\Godot\citybuilder
```
Expected: `SHOTS OK <N>` (N grew by 2). **Read** `tests/visual/shots/roundabout_top.png` and `roundabout_oblique.png`: ring with 4 approaches, yield paint on approaches, no gaps.

- [ ] **Step 3: Commit**

```bash
git add src/Game/VisualShots.cs
git commit -m "feat(m8.75): roundabout gallery scenario"
```

---

### Task 6: Golden-image diffing (`CITYBUILDER_SHOTS_GOLDEN`)

**Files:**
- Modify: `src/Game/VisualShots.cs` — golden set constant, shot recording in `Run()` (`:94-99`), golden update/check block before `SHOTS OK` (`:129`).
- Create (generated): `tests/visual/golden/*.png` (committed).

**Interfaces:**
- Consumes: shot files written by `CaptureAsync` (`VisualShots.cs:97`), `ProjectSettings.GlobalizePath("res://tests/visual/golden")`.
- Produces: env contract — `CITYBUILDER_SHOTS_GOLDEN=update` copies curated shots to `tests/visual/golden/` and prints `GOLDEN UPDATED <n>`; `=check` compares and prints `GOLDEN OK <n>` (exit 0) or `GOLDEN FAIL` + per-shot report (exit 1). Constants `GoldenChannelTolerance = 8`, `GoldenMaxChangedFraction = 0.005f`.

- [ ] **Step 1: Implement in `VisualShots.cs`**

Add near the top of the class:

```csharp
// Golden set: static, deterministic scenarios only (no Traffic — sim timing
// varies run to run). Covers flat paint, junction control, road types with bike
// lanes, elevation, tunnels + x-ray, and roundabouts.
private static readonly string[] GoldenScenarios =
{
    "cross_2lane", "lights_cross", "avenue_mix", "m5_new_types", "bridge",
    "elevated_tee_ramps", "tunnel", "tunnel_xray", "roundabout",
};
private const int GoldenChannelTolerance = 8;          // per-channel, 0..255
private const float GoldenMaxChangedFraction = 0.005f; // 0.5 % of pixels
```

In `Run()`, before the scenario loop add `var goldenShots = new List<string>();` and inside the per-shot loop (`:94-99`), after `count++`:

```csharp
if (System.Array.IndexOf(GoldenScenarios, scenario.Name) >= 0)
    goldenShots.Add($"{scenario.Name}_{shot.Suffix}.png");
```

After the scenario loop, before `GD.Print($"SHOTS OK {count}")`:

```csharp
var goldenMode = OS.GetEnvironment("CITYBUILDER_SHOTS_GOLDEN");
if (goldenMode is "update" or "check")
{
    var goldenDir = ProjectSettings.GlobalizePath("res://tests/visual/golden");
    if (goldenMode == "update")
    {
        System.IO.Directory.CreateDirectory(goldenDir);
        foreach (var name in goldenShots)
            System.IO.File.Copy($"{_dir}/{name}", System.IO.Path.Combine(goldenDir, name), overwrite: true);
        GD.Print($"GOLDEN UPDATED {goldenShots.Count}");
    }
    else
    {
        var failures = new List<string>();
        foreach (var name in goldenShots)
        {
            var goldenPath = System.IO.Path.Combine(goldenDir, name);
            if (!System.IO.File.Exists(goldenPath)) { failures.Add($"{name}: no golden baseline"); continue; }
            var fresh = Image.LoadFromFile($"{_dir}/{name}");
            var golden = Image.LoadFromFile(goldenPath);
            if (fresh.GetSize() != golden.GetSize())
            { failures.Add($"{name}: size {fresh.GetSize()} vs golden {golden.GetSize()}"); continue; }
            fresh.Convert(Image.Format.Rgb8);
            golden.Convert(Image.Format.Rgb8);
            var da = fresh.GetData();
            var db = golden.GetData();
            int changed = 0;
            for (int i = 0; i < da.Length; i += 3)
            {
                if (Math.Abs(da[i] - db[i]) > GoldenChannelTolerance ||
                    Math.Abs(da[i + 1] - db[i + 1]) > GoldenChannelTolerance ||
                    Math.Abs(da[i + 2] - db[i + 2]) > GoldenChannelTolerance)
                    changed++;
            }
            float frac = changed / (float)(da.Length / 3);
            if (frac > GoldenMaxChangedFraction)
                failures.Add($"{name}: {changed} px over tolerance ({frac:P2})");
        }
        if (failures.Count > 0)
        {
            GD.PrintErr("GOLDEN FAIL\n" + string.Join("\n", failures));
            GetTree().Quit(1);
            return;
        }
        GD.Print($"GOLDEN OK {goldenShots.Count}");
    }
}
```

(`byte - byte` promotes to int — `Math.Abs` is safe. `Image.LoadFromFile` is the Godot 4 static loader.)

- [ ] **Step 2: Build, generate baselines, verify check passes**

`dotnet build citybuilder.sln`, then (window required):
```powershell
$env:CITYBUILDER_SHOTS = "tests/visual/shots"; $env:CITYBUILDER_SHOTS_GOLDEN = "update"
& <godot> --path C:\Projects\Godot\citybuilder    # expect: GOLDEN UPDATED ~18, SHOTS OK
$env:CITYBUILDER_SHOTS_GOLDEN = "check"
& <godot> --path C:\Projects\Godot\citybuilder    # expect: GOLDEN OK ~18, SHOTS OK, exit 0
```

- [ ] **Step 3: Prove the failure path without touching code**

```powershell
$env:CITYBUILDER_SHOTS_TINT = "1"   # tints junction meshes → golden scenarios change
& <godot> --path C:\Projects\Godot\citybuilder    # expect: GOLDEN FAIL + per-shot list, exit 1
Remove-Item Env:CITYBUILDER_SHOTS_TINT
```
Confirm the report names the junction-bearing shots and the exit code is 1. Re-run plain `check` afterwards → `GOLDEN OK`.

- [ ] **Step 4: Commit code + baselines**

```bash
git add src/Game/VisualShots.cs tests/visual/golden/
git commit -m "feat(m8.75): golden-image diffing — CITYBUILDER_SHOTS_GOLDEN=check|update"
```

---

### Task 7: Docs, spec sync, roadmap

**Files:**
- Modify: `docs/verification.md` (harness table + new section), `docs/roadmap.md` (M8.75 Done entry), `docs/superpowers/specs/2026-07-20-observability-harness-design.md` (two amendments), `.gitignore` (add `*.csproj.old.*`), `CLAUDE.md` only if the quick-commands table needs the new env vars (it doesn't — verification.md is the right home).

- [ ] **Step 1: Amend the spec** to record two implementation decisions: (a) golden compare is in-harness `Godot.Image` byte comparison, not ImageMagick (no PATH dependency inside the Godot process; same AE-with-tolerance semantics); (b) the smoke dump is gated by `CITYBUILDER_SMOKE_DUMP=<dir>` rather than always-on (keeps default smoke output file-free).

- [ ] **Step 2: Update `docs/verification.md`**
  - Harness table: add rows for `CITYBUILDER_SHOTS_GOLDEN=check` (silent render regressions vs committed baselines) and note `update` regenerates.
  - Screenshot extras list: add `CITYBUILDER_SHOTS_GOLDEN`.
  - New short section "Geometry dumps" documenting `GeometryDump.Svg/Json` (what layers mean, plan-view mapping), the fuzz `fuzz-artifacts/` auto-dump, and `CITYBUILDER_SMOKE_DUMP`. Frame it as: *read the SVG before pixel-guessing a screenshot; read the JSON before writing a state probe.*

- [ ] **Step 3: Update `docs/roadmap.md`** — add the M8.75 Done entry (tooling milestone: GeometryDump SVG/JSON + call sites, roundabout gallery scenario, golden diffing with thresholds, spec deviations noted, verification evidence). Keep "Next up" list unchanged (Zoning stays #1).

- [ ] **Step 4: Add `*.csproj.old.*` to `.gitignore`** (the Godot 4.7 auto-upgrade left `citybuilder.csproj.old.1/.2` untracked noise).

- [ ] **Step 5: Final gates**

Run, in order:
- `dotnet test` filtered quick gate: `dotnet test --filter "FullyQualifiedName!~Fuzz"` → green
- `dotnet test --filter "FullyQualifiedName~FuzzSuiteTests"` (default depth) → green
- `dotnet build citybuilder.sln` → green
- Smoke (headless) → `SMOKE OK`
- Shots with `CITYBUILDER_SHOTS_GOLDEN=check` → `GOLDEN OK`, `SHOTS OK`
- UITEST → `UITEST OK` (untouched, but Main.cs changed — cheap insurance)

- [ ] **Step 6: Commit**

```bash
git add docs/ .gitignore
git commit -m "docs(m8.75): observability harness — verification guide, roadmap, spec sync"
```

---

## Self-review notes

- Spec coverage: SVG layers/labels (T2), JSON (T1), test call site (T1/T2 by construction), fuzz auto-dump (T3), smoke dump (T4), golden set incl. roundabout coverage via new scenario (T5), check/update modes + thresholds (T6), docs/no-KPI/no-manual-chapter (T7). Spec deviations (ImageMagick → in-harness; smoke dump env-gated) are recorded back into the spec in T7.
- Types cross-checked against the explored API: `Nodes/Edges/Roundabouts` dictionaries, `Junction.SurfacePolygon`, `ConnectorConflicts` parallel lists, `ConvertToRoundabout(NodeId, float)`, `Tessellate(in Bezier3, float)`, `FuzzResult` fields, shot path pattern `{name}_{suffix}.png`.
- Uncertain literals called out inline for the implementer to verify at run time: first node/edge id (0 vs 1), `RoadType.Name` strings, `GestureFuzzer.LastNetwork` exact declaration, golden shot count (~18).
