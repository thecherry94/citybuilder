using Godot;

namespace CityBuilder.Game;

/// <summary>Shared materials. Everything is untextured StandardMaterial3D this
/// milestone; culling is off on road surfaces so winding never bites.</summary>
public static class Materials
{
    public static readonly StandardMaterial3D Asphalt = new()
    {
        AlbedoColor = new Color(0.16f, 0.16f, 0.18f),
        Roughness = 0.92f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public static readonly StandardMaterial3D Marking = new()
    {
        AlbedoColor = new Color(0.92f, 0.92f, 0.88f),
        Roughness = 0.7f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public static readonly StandardMaterial3D GhostValid = new()
    {
        AlbedoColor = new Color(0.25f, 0.55f, 1f, 0.45f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        NoDepthTest = false,
    };

    public static readonly StandardMaterial3D GhostInvalid = new()
    {
        AlbedoColor = new Color(1f, 0.25f, 0.25f, 0.45f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public static readonly StandardMaterial3D BulldozeHighlight = new()
    {
        AlbedoColor = new Color(1f, 0.2f, 0.15f, 0.55f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public static readonly StandardMaterial3D DebugLines = new()
    {
        VertexColorUseAsAlbedo = true,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public static readonly StandardMaterial3D SnapIndicator = new()
    {
        AlbedoColor = new Color(1f, 0.85f, 0.2f, 0.9f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
    };
}
