using CityBuilder.Domain.Traffic;
using Godot;

namespace CityBuilder.Game;

/// <summary>Instanced vehicle rendering: one MultiMesh box car per vehicle, colored
/// from a small palette, posed from the sim every frame.</summary>
public partial class TrafficView : MultiMeshInstance3D
{
    private const int Capacity = 1024;

    private static readonly Color[] Palette =
    {
        new(0.75f, 0.12f, 0.10f), new(0.90f, 0.88f, 0.85f), new(0.15f, 0.18f, 0.22f),
        new(0.20f, 0.35f, 0.65f), new(0.55f, 0.57f, 0.60f), new(0.65f, 0.50f, 0.20f),
        new(0.25f, 0.45f, 0.25f), new(0.35f, 0.20f, 0.40f),
    };

    private TrafficSim _sim = null!;

    public void Bind(TrafficSim sim)
    {
        _sim = sim;
        Multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = new BoxMesh
            {
                Size = new Vector3(1.8f, 1.3f, 4.2f),
                Material = new StandardMaterial3D
                {
                    VertexColorUseAsAlbedo = true,
                    Roughness = 0.35f,
                    Metallic = 0.4f,
                },
            },
            InstanceCount = Capacity,
            VisibleInstanceCount = 0,
        };
    }

    public override void _Process(double delta)
    {
        var mm = Multimesh;
        int count = Math.Min(_sim.Vehicles.Count, Capacity);
        mm.VisibleInstanceCount = count;
        for (int i = 0; i < count; i++)
        {
            var v = _sim.Vehicles[i];
            var (pos, fwd) = _sim.Pose(v);
            var origin = pos.ToGodot();
            origin.Y = MeshBuilders.SurfaceY + 0.68f;
            var forward = fwd.ToGodot();
            if (forward.LengthSquared() < 1e-6f)
                forward = Vector3.Forward;
            var basis = Basis.LookingAt(forward.Normalized(), Vector3.Up);
            mm.SetInstanceTransform(i, new Transform3D(basis, origin));
            mm.SetInstanceColor(i, Palette[v.Id & 7]);
        }
    }
}
