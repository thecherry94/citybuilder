using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Geometry;
using CityBuilder.Domain.Network;
using Godot;

namespace CityBuilder.Game;

/// <summary>Derived bridge/embankment structures per edge (M8): sampled spans of the
/// curve classified by height above the flat Y=0 ground — embankment skirts below
/// <see cref="GeoConstants.EmbankmentMax"/>, girder fascia + pillars above. No stored
/// state: everything re-derives from the curve on the same dirty-edge flow
/// <see cref="RoadNetworkView"/> uses. Ground-level edges get no structure node.</summary>
public partial class StructureView : Node3D
{
    private const float SampleStep = 4f;     // metres of arc per span sample
    private const float FasciaDepth = 1.2f;  // girder side-skirt below deck edge
    private const float PillarEvery = 24f;   // metres of arc between pillars
    private const float PillarMinClear = 2f; // no pillars under near-ground ramps
    private const float PillarHalf = 0.8f;   // pillar half-width (square section)

    /// <summary>Render layer carrying the white cut-mask strips: seen only by the
    /// ground-hole mask camera (Main.BuildGround), excluded from the main camera.</summary>
    public const uint CutMaskLayer = 1u << 10;

    private RoadNetwork _network = null!;
    private readonly Dictionary<EdgeId, MeshInstance3D> _instances = new();
    private readonly Dictionary<EdgeId, MeshInstance3D> _maskInstances = new();
    private readonly HashSet<EdgeId> _dirty = new();

    public void Bind(RoadNetwork network)
    {
        _network = network;
        network.Changed += OnChanged;
    }

    private void OnChanged(NetworkDelta delta)
    {
        foreach (var e in delta.EdgesRemoved)
        {
            _dirty.Remove(e);
            if (_instances.Remove(e, out var inst))
                inst.QueueFree();
            if (_maskInstances.Remove(e, out var mask))
                mask.QueueFree();
        }
        foreach (var e in delta.EdgesAdded)
            _dirty.Add(e);
        foreach (var e in delta.EdgesChanged)
            _dirty.Add(e);
    }

    public override void _Process(double delta) => FlushDirty();

    /// <summary>Mesh every edge now — for harnesses that bind after the network is
    /// already built (the screenshot gallery), where no deltas will ever arrive.</summary>
    public void RebuildAll()
    {
        foreach (var id in _network.Edges.Keys)
            _dirty.Add(id);
        FlushDirty();
    }

    public void FlushDirty()
    {
        foreach (var id in _dirty)
            if (_network.Edges.TryGetValue(id, out var edge))
                Rebuild(edge);
        _dirty.Clear();
    }

    private void Rebuild(RoadEdge edge)
    {
        if (_instances.Remove(edge.Id, out var old))
            old.QueueFree();
        if (_maskInstances.Remove(edge.Id, out var oldMask))
            oldMask.QueueFree();

        var mesh = BuildStructures(edge, out var cutMask);
        if (mesh is not null)
        {
            var inst = new MeshInstance3D { Mesh = mesh, Transparency = Dim(edge) };
            AddChild(inst);
            _instances[edge.Id] = inst;
        }
        if (cutMask is not null)
        {
            var mask = new MeshInstance3D
            {
                Mesh = cutMask,
                Layers = CutMaskLayer,
                MaterialOverride = Materials.CutMask,
            };
            AddChild(mask);
            _maskInstances[edge.Id] = mask;
        }
    }

    // ---------------------------------------------------------------- x-ray (M8.5)

    private bool _xray;

    /// <summary>X-ray view: dim above-ground structures (bridges, embankments) so
    /// cuts, portals, and tunnels below stay the visual subject.</summary>
    public void SetXRay(bool on)
    {
        _xray = on;
        foreach (var (id, inst) in _instances)
            if (_network.Edges.TryGetValue(id, out var edge))
                inst.Transparency = Dim(edge);
    }

    private float Dim(RoadEdge e)
        => _xray && (e.Curve.P0.Y > -0.05f || e.Curve.P3.Y > -0.05f) ? 0.55f : 0f;

    private ArrayMesh? BuildStructures(RoadEdge edge, out ArrayMesh? cutMask)
        => BuildStructures(edge.Curve, edge.ArcLength, RoadCatalog.Get(edge.Type).Width,
            out cutMask, edge.Covered, p => CarriagewayObstructed(_network, edge.Id, p));

    /// <summary>True when a pillar carrying a deck at pillarTop would stand inside
    /// another edge's carriageway — the M8 "pillar in the underpass" known limit.
    /// The column occupies XZ from the ground up to its deck, so any other edge whose
    /// deck threads that vertical range within half-width (+ margin) is obstructed.
    /// Trenches below ground are NOT obstructed: the pillar's base stops at Y=0.</summary>
    public static bool CarriagewayObstructed(RoadNetwork network, EdgeId? self, Vector3 pillarTop)
    {
        var probe = new System.Numerics.Vector3(pillarTop.X, 0, pillarTop.Z);
        foreach (var e in network.Edges.Values)
        {
            if (self is { } s && e.Id == s)
                continue;
            var (t, _) = BezierOps.ClosestPoint(e.Curve, probe);
            var at = e.Curve.Point(t);
            if (at.Y <= -0.5f || at.Y >= pillarTop.Y - 0.5f)
                continue; // below the pillar's base, or at/above the deck it carries
            float dxz = new Vector2(at.X - probe.X, at.Z - probe.Z).Length();
            if (dxz <= RoadCatalog.Get(e.Type).Width / 2f + PillarHalf + 1f)
                return true;
        }
        return false;
    }

    /// <summary>One ArrayMesh: surface 0 = earth embankment skirts, surface 1 =
    /// concrete fascia + pillars, surface 2 = retaining walls / portals (M8.5).
    /// Null when the curve never leaves the ground plane.
    /// Public and curve-based so GhostView previews the exact structures a commit
    /// would produce (same thresholds, same code). Below ground, an uncovered edge is
    /// an open cut (walls + coping); a covered edge deeper than PortalDepth is a
    /// tunnel — nothing rendered but the portal faces where the deck crosses that
    /// depth. Portals appear ONLY at internal depth crossings, never at curve ends,
    /// so chains of covered edges (splits) don't sprout portals mid-tunnel.</summary>
    public static ArrayMesh? BuildStructures(in Bezier3 curve, ArcLengthTable arc, float width,
        bool covered = false, Func<Vector3, bool>? pillarObstructed = null)
        => BuildStructures(curve, arc, width, out _, covered, pillarObstructed);

    /// <summary>As above, and additionally returns the cut-opening strips as their own
    /// mesh (null when the edge has no open cut) — rendered white on
    /// <see cref="CutMaskLayer"/> into the ground-hole mask viewport.</summary>
    public static ArrayMesh? BuildStructures(in Bezier3 curve, ArcLengthTable arc, float width,
        out ArrayMesh? cutMask, bool covered = false, Func<Vector3, bool>? pillarObstructed = null)
    {
        cutMask = null;
        float len = arc.TotalLength;
        int n = Mathf.Max(2, (int)(len / SampleStep));
        var pts = new Vector3[n + 1];
        var side = new Vector3[n + 1];
        bool anyStructure = false;
        for (int i = 0; i <= n; i++)
        {
            float t = arc.TAtDistance(len * i / n);
            var p = curve.Point(t);
            var tan = curve.Tangent(t);
            pts[i] = p.ToGodot();
            var s = new Vector3(tan.Z, 0, -tan.X);
            side[i] = s.LengthSquared() > 1e-9f ? s.Normalized() : Vector3.Right;
            if (Mathf.Abs(p.Y) > 0.05f)
                anyStructure = true;
        }
        if (!anyStructure)
            return null;

        var mid = new float[n];
        for (int i = 0; i < n; i++)
            mid[i] = (pts[i].Y + pts[i + 1].Y) / 2f;

        float half = width / 2f;
        var earth = new SurfaceTool();
        earth.Begin(Mesh.PrimitiveType.Triangles);
        var concrete = new SurfaceTool();
        concrete.Begin(Mesh.PrimitiveType.Triangles);
        var wall = new SurfaceTool();
        wall.Begin(Mesh.PrimitiveType.Triangles);
        var cut = new SurfaceTool(); // ground-level opening strips (the "hole" the flat plane can't have)
        cut.Begin(Mesh.PrimitiveType.Triangles);
        bool anyEarth = false, anyConcrete = false, anyWall = false, anyCut = false;

        float sincePillar = PillarEvery; // first eligible spot gets one
        for (int i = 0; i < n; i++)
        {
            float midY = mid[i];
            if (Mathf.Abs(midY) <= 0.05f)
            {
                sincePillar = PillarEvery;
                continue;
            }

            if (midY > 0.05f)
            {
                bool bridge = midY > GeoConstants.EmbankmentMax;
                foreach (float dir in stackalloc float[] { -1f, 1f })
                {
                    var a = pts[i] + side[i] * (half * dir);
                    var b = pts[i + 1] + side[i + 1] * (half * dir);
                    // skirt bottom: ground for embankments, fixed fascia depth for bridges
                    var a2 = bridge ? a with { Y = a.Y - FasciaDepth } : a with { Y = 0 };
                    var b2 = bridge ? b with { Y = b.Y - FasciaDepth } : b with { Y = 0 };
                    var st = bridge ? concrete : earth;
                    AddQuad(st, a, b, b2, a2);
                    if (bridge) anyConcrete = true; else anyEarth = true;
                }

                sincePillar += len / n;
                if (bridge && midY >= PillarMinClear && sincePillar >= PillarEvery)
                {
                    var top = (pts[i] + pts[i + 1]) / 2f;
                    if (pillarObstructed?.Invoke(top) == true)
                    {
                        // blocked spot: keep accumulating so the next clear span takes
                        // the pillar (the "shift"); a long obstruction skips one outright
                        if (sincePillar > 2f * PillarEvery)
                            sincePillar = 0;
                    }
                    else
                    {
                        sincePillar = 0;
                        AddPillar(concrete, top with { Y = top.Y - FasciaDepth / 2f }, side[i]);
                        anyConcrete = true;
                    }
                }
            }
            else
            {
                bool tunnel = covered && midY <= -GeoConstants.PortalDepth;
                if (!tunnel)
                {
                    // open cut: retaining wall from the ground lip DOWN to the deck
                    // edge, plus a narrow coping strip so the cut reads in top-down shots
                    foreach (float dir in stackalloc float[] { -1f, 1f })
                    {
                        var a = pts[i] + side[i] * (half * dir);
                        var b = pts[i + 1] + side[i + 1] * (half * dir);
                        AddQuad(wall, a with { Y = 0 }, b with { Y = 0 }, b, a);
                        var ao = a + side[i] * (0.6f * dir);
                        var bo = b + side[i + 1] * (0.6f * dir);
                        AddQuad(wall, ao with { Y = 0 }, bo with { Y = 0 },
                            b with { Y = 0 }, a with { Y = 0 });
                    }
                    anyWall = true;

                    // the opening itself: the flat ground plane has no hole, so a dark
                    // translucent strip just above it is what makes the pit readable
                    // (and the sunken road faintly visible through it)
                    var aL = (pts[i] - side[i] * (half + 0.6f)) with { Y = 0.02f };
                    var aR = (pts[i] + side[i] * (half + 0.6f)) with { Y = 0.02f };
                    var bL = (pts[i + 1] - side[i + 1] * (half + 0.6f)) with { Y = 0.02f };
                    var bR = (pts[i + 1] + side[i + 1] * (half + 0.6f)) with { Y = 0.02f };
                    AddQuad(cut, aL, aR, bR, bL);
                    anyCut = true;
                }
                else
                {
                    if (i > 0 && mid[i - 1] > -GeoConstants.PortalDepth)
                    {
                        AddPortal(wall, pts[i], side[i], half);       // entry portal
                        anyWall = true;
                    }
                    if (i < n - 1 && mid[i + 1] > -GeoConstants.PortalDepth)
                    {
                        AddPortal(wall, pts[i + 1], side[i + 1], half); // exit portal
                        anyWall = true;
                    }
                }
                sincePillar = PillarEvery; // no pillars below ground
            }
        }

        if (!anyEarth && !anyConcrete && !anyWall && !anyCut)
            return null;
        var mesh = new ArrayMesh();
        if (anyEarth)
        {
            earth.SetMaterial(Materials.Earth);
            earth.Commit(mesh);
        }
        if (anyConcrete)
        {
            concrete.SetMaterial(Materials.Concrete);
            concrete.Commit(mesh);
        }
        if (anyWall)
        {
            wall.SetMaterial(Materials.RetainingWall);
            wall.Commit(mesh);
        }
        if (anyCut)
        {
            cut.SetMaterial(Materials.CutOpening);
            cut.Commit(mesh);
            cutMask = new ArrayMesh();
            cut.Commit(cutMask);
        }
        return mesh;
    }

    /// <summary>Tunnel mouth where a covered deck crosses PortalDepth: a face above
    /// the deck and two wing walls flaring to the ground lip.</summary>
    private static void AddPortal(SurfaceTool st, Vector3 deck, Vector3 side, float half)
    {
        float h = GeoConstants.PortalDepth + 1.5f; // face reaches above the deck
        foreach (float dir in stackalloc float[] { -1f, 1f })
        {
            var edge = deck + side * (half * dir);
            var wing = deck + side * ((half + 2.5f) * dir);
            AddQuad(st, edge with { Y = 0 }, wing with { Y = 0 }, wing, edge); // wing wall
        }
        var l = deck - side * half;
        var r = deck + side * half;
        AddQuad(st, l with { Y = l.Y + h }, r with { Y = r.Y + h }, r, l);     // face
    }

    private static void AddPillar(SurfaceTool st, Vector3 top, Vector3 side)
    {
        var fwd = side.Cross(Vector3.Up).Normalized();
        Vector3[] ring =
        {
            top + side * PillarHalf + fwd * PillarHalf,
            top + side * PillarHalf - fwd * PillarHalf,
            top - side * PillarHalf - fwd * PillarHalf,
            top - side * PillarHalf + fwd * PillarHalf,
        };
        for (int k = 0; k < 4; k++)
        {
            var a = ring[k];
            var b = ring[(k + 1) % 4];
            AddQuad(st, a, b, b with { Y = 0 }, a with { Y = 0 });
        }
    }

    private static void AddQuad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        // double-sided: structures are seen from both sides at grazing angles
        st.AddVertex(a); st.AddVertex(b); st.AddVertex(c);
        st.AddVertex(a); st.AddVertex(c); st.AddVertex(d);
        st.AddVertex(a); st.AddVertex(c); st.AddVertex(b);
        st.AddVertex(a); st.AddVertex(d); st.AddVertex(c);
    }
}
