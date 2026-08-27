namespace Ago.Chat.Domain.Tests;

public class SiteTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WhenPublicKeyIsEmptyOrWhitespace_Throws(string publicKey)
    {
        Assert.Throws<ArgumentException>(() => new Site(new SiteId(Guid.NewGuid()), publicKey, []));
    }

    [Fact]
    public void Constructor_WhenValid_SetsProperties()
    {
        var id = new SiteId(Guid.NewGuid());
        string[] origins = ["https://shop.example"];

        var site = new Site(id, "shop_7f3a", origins);

        Assert.Equal(id, site.Id);
        Assert.Equal("shop_7f3a", site.PublicKey);
        Assert.Equal(origins, site.AllowedOrigins);
    }

    [Fact]
    public void Constructor_WhenValid_DefaultsWidgetConfig()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);

        Assert.Equal(WidgetConfig.Default, site.WidgetConfig);
        Assert.Null(site.WidgetConfig.PrimaryColorHex);
        Assert.Equal(Position.BottomRight, site.WidgetConfig.Position);
    }

    // `11-01`: 11-01's own Done-when - Site.UpdateWidgetConfig rejects a malformed hex color. The
    // rejection actually happens inside WidgetConfig's own constructor (validated once, at
    // construction of the value object - WidgetConfig's own remarks); asserted here too, not just in
    // a WidgetConfig-only test file, because this is the shape a caller building a WidgetConfig to
    // pass into UpdateWidgetConfig actually hits.
    [Theory]
    [InlineData("blue")]
    [InlineData("#fff")]
    [InlineData("#gggggg")]
    [InlineData("123456")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    public void UpdateWidgetConfig_WhenColorIsMalformedHex_Throws(string malformedHex)
    {
        Assert.Throws<ArgumentException>(() => new WidgetConfig(malformedHex, Position.BottomRight));
    }

    [Theory]
    [InlineData("#000000")]
    [InlineData("#FFFFFF")]
    [InlineData("#1a2B3c")]
    public void UpdateWidgetConfig_WhenColorIsValidHex_Accepts(string validHex)
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);
        var now = DateTimeOffset.UtcNow;

        site.UpdateWidgetConfig(new WidgetConfig(validHex, Position.BottomRight), now);

        Assert.Equal(validHex, site.WidgetConfig.PrimaryColorHex);
    }

    [Theory]
    [InlineData(Position.BottomRight)]
    [InlineData(Position.BottomLeft)]
    public void UpdateWidgetConfig_WhenPositionIsEitherValue_Accepts(Position position)
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);
        var now = DateTimeOffset.UtcNow;

        site.UpdateWidgetConfig(new WidgetConfig(null, position), now);

        Assert.Equal(position, site.WidgetConfig.Position);
    }

    [Fact]
    public void UpdateWidgetConfig_WhenCalled_RaisesDomainEventExactlyOnce()
    {
        var id = new SiteId(Guid.NewGuid());
        var site = new Site(id, "shop_7f3a", []);
        var now = DateTimeOffset.UtcNow;

        site.UpdateWidgetConfig(new WidgetConfig("#336699", Position.BottomLeft), now);

        var domainEvent = Assert.Single(site.DomainEvents);
        var raised = Assert.IsType<SiteWidgetConfigUpdated>(domainEvent);
        Assert.Equal(id, raised.SiteId);
        Assert.Equal("shop_7f3a", raised.PublicKey);
        Assert.Equal(now, raised.OccurredAt);
    }

    [Fact]
    public void UpdateWidgetConfig_WhenCalledTwice_RaisesTwoDomainEventsUntilCleared()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);
        var now = DateTimeOffset.UtcNow;

        site.UpdateWidgetConfig(new WidgetConfig(null, Position.BottomLeft), now);
        site.ClearDomainEvents();
        site.UpdateWidgetConfig(new WidgetConfig(null, Position.BottomRight), now);

        Assert.Single(site.DomainEvents);
    }

    // `11-10`: the regression this item's own Done-when names explicitly - every existing tenant, one
    // that has never called UpdateLocale, must read back exactly Locale.En.
    [Fact]
    public void Constructor_WhenValid_DefaultsLocaleToEnglish()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);

        Assert.Equal(Locale.En, site.Locale);
    }

    [Theory]
    [InlineData(Locale.En)]
    [InlineData(Locale.Ru)]
    public void UpdateLocale_WhenCalled_SetsTheLocale(Locale locale)
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);

        site.UpdateLocale(locale, DateTimeOffset.UtcNow);

        Assert.Equal(locale, site.Locale);
    }

    [Fact]
    public void UpdateLocale_WhenCalled_RaisesDomainEventExactlyOnce()
    {
        var id = new SiteId(Guid.NewGuid());
        var site = new Site(id, "shop_7f3a", []);
        var now = DateTimeOffset.UtcNow;

        site.UpdateLocale(Locale.Ru, now);

        var domainEvent = Assert.Single(site.DomainEvents);
        var raised = Assert.IsType<SiteLocaleUpdated>(domainEvent);
        Assert.Equal(id, raised.SiteId);
        Assert.Equal("shop_7f3a", raised.PublicKey);
        Assert.Equal(now, raised.OccurredAt);
    }

    [Fact]
    public void UpdateLocale_WhenCalledTwice_RaisesTwoDomainEventsUntilCleared()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);
        var now = DateTimeOffset.UtcNow;

        site.UpdateLocale(Locale.Ru, now);
        site.ClearDomainEvents();
        site.UpdateLocale(Locale.En, now);

        Assert.Single(site.DomainEvents);
    }
}
