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

    public static readonly StandardMaterial3D Concrete = new()
    {
        // clearly lighter and warmer than the grass so sidewalks read at any angle
        AlbedoColor = new Color(0.68f, 0.67f, 0.63f),
        Roughness = 0.95f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public static readonly StandardMaterial3D BikeLane = new()
    {
        AlbedoColor = new Color(0.45f, 0.16f, 0.13f),
        Roughness = 0.9f,
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

    public static readonly StandardMaterial3D PropMetal = new()
    {
        AlbedoColor = new Color(0.25f, 0.27f, 0.29f),
        Roughness = 0.6f,
        Metallic = 0.4f,
    };

    public static readonly StandardMaterial3D SignRed = new()
    {
        AlbedoColor = new Color(0.75f, 0.10f, 0.10f),
        Roughness = 0.5f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public static readonly StandardMaterial3D SignWhite = new()
    {
        AlbedoColor = new Color(0.95f, 0.95f, 0.93f),
        Roughness = 0.5f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public static readonly StandardMaterial3D SignYellow = new()
    {
        AlbedoColor = new Color(0.95f, 0.78f, 0.10f),
        Roughness = 0.5f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public static readonly StandardMaterial3D LampRed = new()
    {
        AlbedoColor = new Color(0.9f, 0.12f, 0.10f),
        EmissionEnabled = true,
        Emission = new Color(0.9f, 0.12f, 0.10f),
        EmissionEnergyMultiplier = 0.6f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public static readonly StandardMaterial3D LampAmber = new()
    {
        AlbedoColor = new Color(0.95f, 0.65f, 0.10f),
        EmissionEnabled = true,
        Emission = new Color(0.95f, 0.65f, 0.10f),
        EmissionEnergyMultiplier = 0.3f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public static readonly StandardMaterial3D LampGreen = new()
    {
        AlbedoColor = new Color(0.10f, 0.80f, 0.25f),
        EmissionEnabled = true,
        Emission = new Color(0.10f, 0.80f, 0.25f),
        EmissionEnergyMultiplier = 0.6f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public static readonly StandardMaterial3D DebugLines = new()
    {
        VertexColorUseAsAlbedo = true,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public static readonly StandardMaterial3D SnapIndicator = new()
    {
        AlbedoColor = new Color(1f, 0.85f, 0.2f, 0.9f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
    };

    public static readonly StandardMaterial3D SnapNode = new()
    {
        // node lock ring — the decisive-capture signal, brighter than everything else
        AlbedoColor = new Color(0.35f, 0.95f, 1f, 0.95f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public static readonly StandardMaterial3D SnapAccent = new()
    {
        AlbedoColor = new Color(0.55f, 0.8f, 1f, 0.9f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };
}
