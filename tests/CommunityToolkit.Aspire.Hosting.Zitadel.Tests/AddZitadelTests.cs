using Aspire.Hosting;
using Xunit;

namespace CommunityToolkit.Aspire.Hosting.Zitadel.Tests;

public class AddZitadelTests
{
    [Fact]
    public void AddZitadel_CreatesZitadelResource()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();

        // Act
        var resource = builder.AddZitadel("zitadel");

        // Assert
        Assert.NotNull(resource);
        Assert.Equal("zitadel", resource.Resource.Name);
    }
}
