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

    private RoadNetwork _network = null!;
    private readonly Dictionary<EdgeId, MeshInstance3D> _instances = new();
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

        var mesh = BuildStructures(edge);
        if (mesh is null)
            return;
        var inst = new MeshInstance3D { Mesh = mesh };
        AddChild(inst);
        _instances[edge.Id] = inst;
    }

    /// <summary>One ArrayMesh: surface 0 = earth embankment skirts, surface 1 =
    /// concrete fascia + pillars. Null when the edge never leaves the ground.</summary>
    private static ArrayMesh? BuildStructures(RoadEdge edge)
    {
        float len = edge.ArcLength.TotalLength;
        int n = Mathf.Max(2, (int)(len / SampleStep));
        var pts = new Vector3[n + 1];
        var side = new Vector3[n + 1];
        bool anyElevated = false;
        for (int i = 0; i <= n; i++)
        {
            float t = edge.ArcLength.TAtDistance(len * i / n);
            var p = edge.Curve.Point(t);
            var tan = edge.Curve.Tangent(t);
            pts[i] = p.ToGodot();
            var s = new Vector3(tan.Z, 0, -tan.X);
            side[i] = s.LengthSquared() > 1e-9f ? s.Normalized() : Vector3.Right;
            if (p.Y > 0.05f)
                anyElevated = true;
        }
        if (!anyElevated)
            return null;

        float half = RoadCatalog.Get(edge.Type).Width / 2f;
        var earth = new SurfaceTool();
        earth.Begin(Mesh.PrimitiveType.Triangles);
        var concrete = new SurfaceTool();
        concrete.Begin(Mesh.PrimitiveType.Triangles);
        bool anyEarth = false, anyConcrete = false;

        float sincePillar = PillarEvery; // first eligible spot gets one
        for (int i = 0; i < n; i++)
        {
            float midY = (pts[i].Y + pts[i + 1].Y) / 2f;
            if (midY <= 0.05f)
            {
                sincePillar = PillarEvery;
                continue;
            }
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
                sincePillar = 0;
                var top = (pts[i] + pts[i + 1]) / 2f;
                AddPillar(concrete, top with { Y = top.Y - FasciaDepth / 2f }, side[i]);
                anyConcrete = true;
            }
        }

        if (!anyEarth && !anyConcrete)
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
        return mesh;
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
