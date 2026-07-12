namespace CityBuilder.Game;

/// <summary>Conversions between the domain's System.Numerics vectors and Godot's.</summary>
public static class VecExt
{
    public static Godot.Vector3 ToGodot(this System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
    public static System.Numerics.Vector3 ToNumerics(this Godot.Vector3 v) => new(v.X, v.Y, v.Z);
}
