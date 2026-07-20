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

    /// <summary>Road paint. A shader rather than a StandardMaterial3D: at grazing
    /// view angles the thin paint quads foreshorten below a pixel and MSAA shreds
    /// them into floating white streaks over distant decks. The shader cuts the
    /// paint (alpha scissor — stays in the opaque pass, no sorting) once the view
    /// ray is within ~3° of the surface, where paint is illegible anyway.</summary>
    public static readonly ShaderMaterial Marking = new()
    {
        Shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode cull_disabled;
                void fragment() {
                    ALBEDO = vec3(0.92, 0.92, 0.88);
                    ROUGHNESS = 0.7;
                    ALPHA = abs(dot(normalize(NORMAL), normalize(VIEW))) < 0.075 ? 0.0 : 1.0;
                    ALPHA_SCISSOR_THRESHOLD = 0.5;
                }
                """,
        },
    };

    /// <summary>Embankment fill under low elevated roads (M8 structures).</summary>
    public static readonly StandardMaterial3D Earth = new()
    {
        AlbedoColor = new Color(0.42f, 0.36f, 0.28f),
        Roughness = 1f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public static readonly StandardMaterial3D Concrete = new()
    {
        // clearly lighter and warmer than the grass so sidewalks read at any angle
        AlbedoColor = new Color(0.68f, 0.67f, 0.63f),
        Roughness = 0.95f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    /// <summary>Light shade over an open cut's mouth. The real hole in the ground is
    /// punched by the cut-mask discard (see Main.BuildGround); this strip only adds a
    /// hint of depth-shadow so the pit doesn't read as brightly lit as the surface.</summary>
    public static readonly StandardMaterial3D CutOpening = new()
    {
        AlbedoColor = new Color(0.06f, 0.07f, 0.08f, 0.30f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        Roughness = 1f,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    /// <summary>Cut-opening strips re-rendered white into the top-down mask viewport;
    /// the ground shader discards where the mask is set, making the hole real.</summary>
    public static readonly StandardMaterial3D CutMask = new()
    {
        AlbedoColor = new Color(1f, 1f, 1f),
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
    };

    public static readonly StandardMaterial3D RetainingWall = new()
    {
        // colder and darker than sidewalk concrete so cuts read as engineered walls
        AlbedoColor = new Color(0.48f, 0.47f, 0.45f),
        Roughness = 0.9f,
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

    /// <summary>Ground projection under an elevated ghost — a shadow, not a road:
    /// dark, faint, and never occluding (no depth write).</summary>
    public static readonly StandardMaterial3D GhostShadow = new()
    {
        AlbedoColor = new Color(0.05f, 0.08f, 0.15f, 0.25f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        DisableReceiveShadows = true,
        RenderPriority = -1,
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
