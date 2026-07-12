using Godot;

namespace CityBuilder.Game;

/// <summary>RTS camera: WASD pans on the ground plane, wheel zooms, Q/E or middle-drag
/// rotates. Yaw lives on this node, pitch on a child, the camera hangs back at
/// Distance.</summary>
public partial class CameraRig : Node3D
{
    private Node3D _pitch = null!;
    private Camera3D _camera = null!;
    private bool _rotating;

    public float Distance { get; private set; } = 120f;
    public Camera3D Camera => _camera;

    public override void _Ready()
    {
        _pitch = new Node3D { Name = "Pitch" };
        AddChild(_pitch);
        _pitch.Rotation = new Vector3(Mathf.DegToRad(-55f), 0, 0);
        _camera = new Camera3D { Name = "Camera", Far = 4000f };
        _pitch.AddChild(_camera);
        UpdateCamera();
    }

    private void UpdateCamera() => _camera.Position = new Vector3(0, 0, Distance);

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        var move = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W)) move.Z -= 1;
        if (Input.IsKeyPressed(Key.S)) move.Z += 1;
        if (Input.IsKeyPressed(Key.A)) move.X -= 1;
        if (Input.IsKeyPressed(Key.D)) move.X += 1;
        if (move != Vector3.Zero)
        {
            move = move.Normalized().Rotated(Vector3.Up, Rotation.Y);
            Position += move * Distance * 0.8f * dt;
        }
        float spin = 0;
        if (Input.IsKeyPressed(Key.Q)) spin += 1;
        if (Input.IsKeyPressed(Key.E)) spin -= 1;
        if (spin != 0)
            RotateY(spin * 1.2f * dt);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                Distance = Mathf.Clamp(Distance * 0.9f, 10f, 500f);
                UpdateCamera();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                Distance = Mathf.Clamp(Distance * 1.1f, 10f, 500f);
                UpdateCamera();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle } mb:
                _rotating = mb.Pressed;
                break;
            case InputEventMouseMotion mm when _rotating:
                RotateY(-mm.Relative.X * 0.005f);
                var p = _pitch.Rotation;
                p.X = Mathf.Clamp(p.X - mm.Relative.Y * 0.004f, Mathf.DegToRad(-85f), Mathf.DegToRad(-15f));
                _pitch.Rotation = p;
                break;
        }
    }

    /// <summary>Where the mouse ray hits the Y=0 plane, if it does.</summary>
    public Vector3? MouseGroundPoint()
    {
        var vp = GetViewport();
        var mp = vp.GetMousePosition();
        var origin = _camera.ProjectRayOrigin(mp);
        var dir = _camera.ProjectRayNormal(mp);
        if (Mathf.Abs(dir.Y) < 1e-6f)
            return null;
        float t = -origin.Y / dir.Y;
        return t < 0 ? null : origin + dir * t;
    }

    /// <summary>Ground point straight below the screen center — used as a fallback anchor.</summary>
    public float SnapRadius() => Mathf.Clamp(Distance * 0.02f, 1f, 20f);
}
