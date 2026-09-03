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
        // `16-04`: every existing tenant, and every freshly created one, shows no processing notice -
        // an AGO-authored default would be AGO asserting a legal position on the tenant's behalf, which
        // this item must not do (WidgetConfig's own remarks).
        Assert.Null(site.WidgetConfig.NoticeText);
        Assert.Null(site.WidgetConfig.NoticeUrl);
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

    // `16-04`: 16-04's own Scope - the URL is validated `https://` only, the same reflex `6-03`
    // applied to webhook endpoints (minus that validator's SSRF/private-range check, which does not
    // apply here - WidgetConfig's own remarks explain why: this URL is only ever handed to a visitor's
    // browser, never fetched by this server).
    [Theory]
    [InlineData("http://tenant.example/privacy")]
    [InlineData("ftp://tenant.example/privacy")]
    [InlineData("tenant.example/privacy")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateWidgetConfig_WhenNoticeUrlIsNotAbsoluteHttps_Throws(string malformedUrl)
    {
        Assert.Throws<ArgumentException>(() => new WidgetConfig(null, Position.BottomRight, null, malformedUrl));
    }

    [Fact]
    public void UpdateWidgetConfig_WhenNoticeUrlIsValidHttps_Accepts()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);
        var now = DateTimeOffset.UtcNow;

        site.UpdateWidgetConfig(
            new WidgetConfig(null, Position.BottomRight, null, "https://tenant.example/privacy"), now);

        Assert.Equal("https://tenant.example/privacy", site.WidgetConfig.NoticeUrl);
    }

    // `16-04`: whitespace-only text is rejected rather than silently stored - a tenant who meant "no
    // notice" should leave the field null (WidgetConfig's own remarks: "leave it null to show no
    // notice"), not save three spaces and have the widget render an empty-looking bar.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateWidgetConfig_WhenNoticeTextIsWhitespaceOnly_Throws(string malformedText)
    {
        Assert.Throws<ArgumentException>(() => new WidgetConfig(null, Position.BottomRight, malformedText, null));
    }

    [Fact]
    public void UpdateWidgetConfig_WhenNoticeTextExceedsMaxLength_Throws()
    {
        var tooLong = new string('a', WidgetConfig.MaxNoticeTextLength + 1);

        Assert.Throws<ArgumentException>(() => new WidgetConfig(null, Position.BottomRight, tooLong, null));
    }

    [Fact]
    public void UpdateWidgetConfig_WhenNoticeTextAndUrlAreValid_Accepts()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);
        var now = DateTimeOffset.UtcNow;
        const string text = "We use your messages to answer your questions. Read more about how we handle them.";

        site.UpdateWidgetConfig(
            new WidgetConfig(null, Position.BottomRight, text, "https://tenant.example/privacy"), now);

        Assert.Equal(text, site.WidgetConfig.NoticeText);
        Assert.Equal("https://tenant.example/privacy", site.WidgetConfig.NoticeUrl);
    }

    // `16-04`'s own Scope - "Both optional... a tenant that has not want a notice in the widget must be
    // able to leave them empty." Text with no link, and a link with no text, are both legitimate.
    [Fact]
    public void UpdateWidgetConfig_WhenOnlyNoticeTextIsSet_AcceptsWithNullUrl()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);
        var now = DateTimeOffset.UtcNow;

        site.UpdateWidgetConfig(new WidgetConfig(null, Position.BottomRight, "We read what you send us.", null), now);

        Assert.Equal("We read what you send us.", site.WidgetConfig.NoticeText);
        Assert.Null(site.WidgetConfig.NoticeUrl);
    }

    [Fact]
    public void UpdateWidgetConfig_WhenOnlyNoticeUrlIsSet_AcceptsWithNullText()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);
        var now = DateTimeOffset.UtcNow;

        site.UpdateWidgetConfig(
            new WidgetConfig(null, Position.BottomRight, null, "https://tenant.example/privacy"), now);

        Assert.Null(site.WidgetConfig.NoticeText);
        Assert.Equal("https://tenant.example/privacy", site.WidgetConfig.NoticeUrl);
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

    // `13-02`/`13-08`: the free-tier defaults every existing and newly-registered site reads until a
    // real payment writes something else - `13-01`'s own regression, restated here alongside every
    // other "what does a fresh Site look like" assertion in this file. `13-08` raised the seat default
    // from 1 to 2 - "the free tier is two operators with two months of history."
    [Fact]
    public void Constructor_WhenValid_DefaultsToFreeTierWithTwoSeats()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);

        Assert.Equal("free", site.Tier);
        Assert.Equal(2, site.SeatLimit);
    }

    [Fact]
    public void ActivateSubscription_WhenCalled_SetsTierAndSeatLimit()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);

        site.ActivateSubscription(SubscriptionTierBands.Growth, 25, DateTimeOffset.UtcNow);

        Assert.Equal(SubscriptionTierBands.Growth, site.Tier);
        Assert.Equal(25, site.SeatLimit);
    }

    [Fact]
    public void ActivateSubscription_WhenCalled_RaisesDomainEventExactlyOnce()
    {
        var id = new SiteId(Guid.NewGuid());
        var site = new Site(id, "shop_7f3a", []);
        var now = DateTimeOffset.UtcNow;

        site.ActivateSubscription(SubscriptionTierBands.Starter, 5, now);

        var domainEvent = Assert.Single(site.DomainEvents);
        var raised = Assert.IsType<SiteSubscriptionActivated>(domainEvent);
        Assert.Equal(id, raised.SiteId);
        Assert.Equal("shop_7f3a", raised.PublicKey);
        Assert.Equal(SubscriptionTierBands.Starter, raised.Tier);
        Assert.Equal(5, raised.SeatLimit);
        Assert.Equal(now, raised.OccurredAt);
    }

    [Fact]
    public void ActivateSubscription_WhenCalledTwice_RaisesTwoDomainEventsUntilCleared()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);
        var now = DateTimeOffset.UtcNow;

        site.ActivateSubscription(SubscriptionTierBands.Starter, 5, now);
        site.ClearDomainEvents();
        site.ActivateSubscription(SubscriptionTierBands.Growth, 25, now);

        Assert.Single(site.DomainEvents);
        Assert.Equal(SubscriptionTierBands.Growth, site.Tier);
        Assert.Equal(25, site.SeatLimit);
    }

    // `18-03`: the canned-response library - every existing tenant, one that has never called
    // UpdateCannedResponses, must read back an empty list rather than throwing or returning null.
    [Fact]
    public void Constructor_WhenValid_DefaultsCannedResponsesToEmpty()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);

        Assert.Empty(site.CannedResponses);
    }

    [Fact]
    public void UpdateCannedResponses_WhenCalled_SetsTheList()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);
        CannedResponse[] responses =
        [
            new("Refund policy", "Refunds take three working days."),
            new("Greeting", "Hi, how can I help?"),
        ];

        site.UpdateCannedResponses(responses);

        Assert.Equal(responses, site.CannedResponses);
    }

    [Fact]
    public void UpdateCannedResponses_ReplacesRatherThanAppends()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);
        site.UpdateCannedResponses([new CannedResponse("Greeting", "Hi there.")]);

        site.UpdateCannedResponses([new CannedResponse("Refund policy", "Three days.")]);

        var only = Assert.Single(site.CannedResponses);
        Assert.Equal("Refund policy", only.Title);
    }

    [Fact]
    public void UpdateCannedResponses_WithMoreThanTheCap_Throws()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);
        var tooMany = Enumerable
            .Range(0, CannedResponse.MaxCount + 1)
            .Select(i => new CannedResponse($"Title {i}", "Reply text."))
            .ToList();

        Assert.Throws<ArgumentException>(() => site.UpdateCannedResponses(tooMany));
    }

    // The architectural decision this item's own remarks (Site.UpdateCannedResponses) make explicit:
    // unlike every other Site update method in this file, this one raises no domain event, because
    // nothing downstream ever needs telling - see that method's own doc comment for the full
    // reasoning. Asserted here so a future change that "helpfully" adds one back gets caught by a
    // failing test, not just a comment nobody re-reads.
    [Fact]
    public void UpdateCannedResponses_WhenCalled_RaisesNoDomainEvent()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);

        site.UpdateCannedResponses([new CannedResponse("Greeting", "Hi there.")]);

        Assert.Empty(site.DomainEvents);
    }
}
