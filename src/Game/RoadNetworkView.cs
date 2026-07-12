using CityBuilder.Domain.Catalog;
using CityBuilder.Domain.Network;
using Godot;

namespace CityBuilder.Game;

/// <summary>Renders the road network. Purely reactive: subscribes to network change
/// events, marks entries dirty, regenerates at most once per frame. Holds no
/// authoritative state.</summary>
public partial class RoadNetworkView : Node3D
{
    private RoadNetwork _network = null!;
    private readonly Dictionary<EdgeId, MeshInstance3D> _edgeInstances = new();
    private readonly Dictionary<NodeId, MeshInstance3D> _nodeInstances = new();
    private readonly HashSet<EdgeId> _dirtyEdges = new();
    private readonly HashSet<NodeId> _dirtyNodes = new();

    public void Bind(RoadNetwork network)
    {
        _network = network;
        network.Changed += OnChanged;
    }

    private void OnChanged(NetworkDelta delta)
    {
        foreach (var e in delta.EdgesRemoved)
        {
            _dirtyEdges.Remove(e);
            if (_edgeInstances.Remove(e, out var inst))
                inst.QueueFree();
        }
        foreach (var n in delta.NodesRemoved)
        {
            _dirtyNodes.Remove(n);
            if (_nodeInstances.Remove(n, out var inst))
                inst.QueueFree();
        }
        foreach (var e in delta.EdgesAdded)
            _dirtyEdges.Add(e);
        foreach (var n in delta.NodesAdded.Concat(delta.NodesChanged))
        {
            _dirtyNodes.Add(n);
            // junction cuts changed: edges meeting here need remeshing too
            if (_network.Nodes.TryGetValue(n, out var node))
                foreach (var e in node.Edges)
                    _dirtyEdges.Add(e);
        }
    }

    public override void _Process(double delta) => FlushDirty();

    /// <summary>Regenerate all dirty meshes now (also used by the smoke test).</summary>
    public void FlushDirty()
    {
        foreach (var id in _dirtyEdges)
            if (_network.Edges.TryGetValue(id, out var edge))
                RebuildEdge(edge);
        _dirtyEdges.Clear();

        foreach (var id in _dirtyNodes)
            if (_network.Nodes.TryGetValue(id, out var node))
                RebuildNode(node);
        _dirtyNodes.Clear();
    }

    private void RebuildEdge(RoadEdge edge)
    {
        float tStart = 0f, tEnd = 1f;
        if (_network.Nodes.TryGetValue(edge.StartNode, out var sn)
            && sn.Junction.CutT.TryGetValue(edge.Id, out var a))
            tStart = a;
        if (_network.Nodes.TryGetValue(edge.EndNode, out var en)
            && en.Junction.CutT.TryGetValue(edge.Id, out var b))
            tEnd = b;

        var mesh = MeshBuilders.BuildEdgeMesh(edge, RoadCatalog.Get(edge.Type), tStart, tEnd);
        var inst = GetOrCreate(_edgeInstances, edge.Id, $"edge_{edge.Id.Value}");
        inst.Mesh = mesh;
        if (mesh is not null)
        {
            inst.SetSurfaceOverrideMaterial(0, Materials.Asphalt);
            if (mesh.GetSurfaceCount() > 1)
                inst.SetSurfaceOverrideMaterial(1, Materials.Marking);
        }
    }

    private void RebuildNode(RoadNode node)
    {
        var mesh = MeshBuilders.BuildJunctionMesh(node, _network.Edges);
        var inst = GetOrCreate(_nodeInstances, node.Id, $"node_{node.Id.Value}");
        inst.Mesh = mesh;
        if (mesh is not null)
            inst.SetSurfaceOverrideMaterial(0, DebugTint ? Materials.SnapIndicator : Materials.Asphalt);
    }

    private MeshInstance3D GetOrCreate<TKey>(Dictionary<TKey, MeshInstance3D> map, TKey key, string name)
        where TKey : notnull
    {
        if (map.TryGetValue(key, out var inst))
            return inst;
        inst = new MeshInstance3D { Name = name };
        AddChild(inst);
        map[key] = inst;
        return inst;
    }

    /// <summary>Tint an edge (bulldoze hover); pass null to clear.</summary>
    public void HighlightEdge(EdgeId? id)
    {
        foreach (var (eid, inst) in _edgeInstances)
            inst.MaterialOverride = eid.Equals(id) ? Materials.BulldozeHighlight : null;
    }

    public int EdgeInstanceCount => _edgeInstances.Count;
    public int NodeInstanceCount => _nodeInstances.Count;

    /// <summary>Diagnostic: tint junction surfaces so overlaps are attributable.</summary>
    public bool DebugTint { get; set; }
}
