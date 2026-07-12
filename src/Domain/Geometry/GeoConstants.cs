namespace CityBuilder.Domain.Geometry;

public static class GeoConstants
{
    /// <summary>Geometric epsilon in meters.</summary>
    public const float Eps = 1e-4f;

    /// <summary>Minimum buildable edge length in meters.</summary>
    public const float MinEdgeLength = 4f;

    /// <summary>Max deviation allowed when merging two edges back into one.</summary>
    public const float MergeTolerance = 0.05f;
}
