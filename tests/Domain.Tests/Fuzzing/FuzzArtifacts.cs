using CityBuilder.Domain.Diagnostics;
using CityBuilder.Domain.Network;

namespace CityBuilder.Domain.Tests.Fuzzing;

/// <summary>
/// On a fuzz failure, drop SVG+JSON geometry dumps next to the test binaries so the
/// failing seed ships with a picture, not just an action tail.
/// </summary>
internal static class FuzzArtifacts
{
    internal static string DumpOnFailure(RoadNetwork? network, string tag)
    {
        if (network is null)
            return "";
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "fuzz-artifacts");
        Directory.CreateDirectory(dir);
        var baseName = Path.Combine(dir, tag);
        GeometryDump.SvgToFile(network, baseName + ".svg");
        GeometryDump.JsonToFile(network, baseName + ".json");
        return $"\ngeometry dumps: {baseName}.svg|.json";
    }
}
