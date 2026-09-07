using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Domain;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Core.Tests;

public sealed class CorePrimitivesTests
{
    [Fact]
    public void BaseEntityExposesAnIdentifier()
    {
        var entity = new TestEntity { Id = 42 };

        Assert.Equal(42, entity.Id);
    }

    [Fact]
    public void ConfigDefaultsToItsRuntimeTypeNameAndFirstOrder()
    {
        IConfig config = new TestConfig();

        Assert.Equal(nameof(TestConfig), config.Name);
        Assert.Equal(1, config.Order);
    }

    [Fact]
    public void SystemClockReturnsUtcTime()
    {
        var before = DateTime.UtcNow;
        var current = new SystemClock().UtcNow;
        var after = DateTime.UtcNow;

        Assert.InRange(current, before, after);
        Assert.Equal(DateTimeKind.Utc, current.Kind);
    }

    private sealed class TestEntity : BaseEntity;

    private sealed class TestConfig : IConfig;
}