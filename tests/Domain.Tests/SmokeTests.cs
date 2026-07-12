using CityBuilder.Domain.Geometry;
using Xunit;

namespace CityBuilder.Domain.Tests;

public class SmokeTests
{
    [Fact]
    public void DomainAssemblyIsReferenced()
    {
        Assert.Equal(4f, GeoConstants.MinEdgeLength);
    }
}
