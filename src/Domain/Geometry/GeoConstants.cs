namespace CityBuilder.Domain.Geometry;

public static class GeoConstants
{
    /// <summary>Geometric epsilon in meters.</summary>
    public const float Eps = 1e-4f;

    /// <summary>Minimum buildable edge length in meters.</summary>
    public const float MinEdgeLength = 4f;

    /// <summary>Max deviation allowed when merging two edges back into one.</summary>
    public const float MergeTolerance = 0.05f;

    /// <summary>Crossings/legs within this ΔY are coplanar: they junction (M8).</summary>
    public const float JunctionYTolerance = 0.6f;

    /// <summary>ΔY at an XZ crossing at or above this is grade-separated: legal, no junction (M8).</summary>
    public const float MinClearance = 4.7f;

    /// <summary>Deck height above ground below which a road renders as embankment, above as bridge (M8).</summary>
    public const float EmbankmentMax = 1.0f;

    /// <summary>Editor elevation clamp (domain itself is unclamped/signed) (M8).</summary>
    public const float MaxElevation = 50f;

    /// <summary>Editor depth clamp below ground: elevation ∈ [−MaxDepth, MaxElevation] (M8.5).</summary>
    public const float MaxDepth = 50f;

    /// <summary>Deck depth at/below which a covered edge's span renders as tunnel
    /// (portal face where the deck crosses this depth). Shallower covered spans stay
    /// open cut — a portal needs headroom (M8.5).</summary>
    public const float PortalDepth = 3.0f;
}
