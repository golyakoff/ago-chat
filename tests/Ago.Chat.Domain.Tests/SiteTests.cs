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
        // `24-05`: every existing tenant, and every freshly created one, does not require a recorded
        // consent before accepting a contact detail - the unchanged-default requirement the backlog
        // item's own Scope names explicitly.
        Assert.False(site.WidgetConfig.RequireContactConsent);
        // `23-63`: every existing tenant, and every freshly created one, has a motionless launcher -
        // the item's own Decision: "a setting, off unless the tenant turns it on."
        Assert.False(site.WidgetConfig.AttractAttention);
    }

    [Fact]
    public void UpdateWidgetConfig_WhenAttractAttentionIsSetTrue_Accepts()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);
        var now = DateTimeOffset.UtcNow;

        site.UpdateWidgetConfig(new WidgetConfig(null, Position.BottomRight, attractAttention: true), now);

        Assert.True(site.WidgetConfig.AttractAttention);
    }

    [Fact]
    public void UpdateWidgetConfig_WhenAttractAttentionIsOmitted_DefaultsFalse()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);
        var now = DateTimeOffset.UtcNow;
        site.UpdateWidgetConfig(new WidgetConfig(null, Position.BottomRight, attractAttention: true), now);

        // The identical "a later call that omits the flag must not silently carry the old value
        // forward" guard RequireContactConsent's own test states, for the sixth field on this same type.
        site.UpdateWidgetConfig(new WidgetConfig(null, Position.BottomLeft), now);

        Assert.False(site.WidgetConfig.AttractAttention);
    }

    [Fact]
    public void UpdateWidgetConfig_WhenRequireContactConsentIsSetTrue_Accepts()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);
        var now = DateTimeOffset.UtcNow;

        site.UpdateWidgetConfig(new WidgetConfig(null, Position.BottomRight, requireContactConsent: true), now);

        Assert.True(site.WidgetConfig.RequireContactConsent);
    }

    [Fact]
    public void UpdateWidgetConfig_WhenRequireContactConsentIsOmitted_DefaultsFalse()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);
        var now = DateTimeOffset.UtcNow;
        site.UpdateWidgetConfig(new WidgetConfig(null, Position.BottomRight, requireContactConsent: true), now);

        // A later call that does not mention the flag at all (the console's own launcher-only save,
        // say) must not silently carry the old value forward by accident - WidgetConfig's constructor
        // parameter defaults to false, so a caller must say true explicitly to keep it on.
        site.UpdateWidgetConfig(new WidgetConfig(null, Position.BottomLeft), now);

        Assert.False(site.WidgetConfig.RequireContactConsent);
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
        // `25-25`: the free tier's own single Administrator (`ago-business` decision `0011`) - a fresh
        // Site never needs a fifth constructor argument to read this correctly, since it is derived
        // from the default `tier` alone (`Site.AdminLimit`'s own remarks).
        Assert.Equal(SubscriptionTierBands.FreeAdminsIncluded, site.AdminLimit);
    }

    /// <summary>`25-25`: a `Site` constructed directly with an explicit paid `tier` - the shape every
    /// existing test that seeds a paid-tier `Site` without going through `ActivateSubscription` already
    /// uses (`ToggleOperatorSeatHandlerTests`, `GetSeatAssignmentSummaryHandlerTests`, and others) - gets
    /// the correct Administrator ceiling for that tier too, with no fifth constructor argument of its
    /// own to keep in sync.</summary>
    [Theory]
    [InlineData("free", 1)]
    [InlineData("starter", 2)]
    [InlineData("growth", 2)]
    public void Constructor_WithAnExplicitTier_DerivesAdminLimitFromIt(string tier, int expectedAdminLimit)
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", [], tier: tier, seatLimit: 5);

        Assert.Equal(expectedAdminLimit, site.AdminLimit);
    }

    [Fact]
    public void ActivateSubscription_WhenCalled_SetsTierAndSeatLimit()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);

        site.ActivateSubscription(SubscriptionTierBands.Growth, 25, DateTimeOffset.UtcNow);

        Assert.Equal(SubscriptionTierBands.Growth, site.Tier);
        Assert.Equal(25, site.SeatLimit);
        // `25-25`: every paid tier includes two Administrators, regardless of seat count - the same
        // number `SubscriptionTierBands.BusinessAdminsIncluded` names for `Starter`.
        Assert.Equal(SubscriptionTierBands.BusinessAdminsIncluded, site.AdminLimit);
    }

    /// <summary>`25-25`: a downgrade to the free tier lowers `AdminLimit` back to one, the identical
    /// derivation `ActivateSubscription`'s own remarks describe for every other call - proven
    /// separately from the "raises to Growth" case above because a downgrade is the direction
    /// `decisions/0006`'s own "never block a downgrade" precedent is actually about.</summary>
    [Fact]
    public void ActivateSubscription_WhenDowngradingToFree_LowersAdminLimitBackToOne()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);
        site.ActivateSubscription(SubscriptionTierBands.Growth, 25, DateTimeOffset.UtcNow);
        Assert.Equal(SubscriptionTierBands.BusinessAdminsIncluded, site.AdminLimit);

        site.ActivateSubscription("free", 1, DateTimeOffset.UtcNow);

        Assert.Equal("free", site.Tier);
        Assert.Equal(SubscriptionTierBands.FreeAdminsIncluded, site.AdminLimit);
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

    // `23-05`: the regression this item's own Done-when implies - every existing tenant, one that has
    // never called UpdateAssignmentPenalty, must read back exactly 120 (two minutes), the same value
    // the database column defaults to, so no row written before this column existed silently behaves
    // differently from one written after.
    [Fact]
    public void Constructor_WhenValid_DefaultsAssignmentPenaltyToTwoMinutes()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);

        Assert.Equal(120, site.AssignmentPenaltySeconds);
    }

    [Fact]
    public void UpdateAssignmentPenalty_WhenCalled_SetsTheValue()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);

        site.UpdateAssignmentPenalty(60, DateTimeOffset.UtcNow);

        Assert.Equal(60, site.AssignmentPenaltySeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void UpdateAssignmentPenalty_WhenNotPositive_Throws(int seconds)
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);

        Assert.Throws<ArgumentOutOfRangeException>(() => site.UpdateAssignmentPenalty(seconds, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void UpdateAssignmentPenalty_WhenCalled_RaisesDomainEventExactlyOnce()
    {
        var id = new SiteId(Guid.NewGuid());
        var site = new Site(id, "shop_7f3a", []);
        var now = DateTimeOffset.UtcNow;

        site.UpdateAssignmentPenalty(300, now);

        var domainEvent = Assert.Single(site.DomainEvents);
        var raised = Assert.IsType<SiteAssignmentPenaltyUpdated>(domainEvent);
        Assert.Equal(id, raised.SiteId);
        Assert.Equal("shop_7f3a", raised.PublicKey);
        Assert.Equal(now, raised.OccurredAt);
    }

    [Fact]
    public void UpdateAssignmentPenalty_WhenCalledTwice_RaisesTwoDomainEventsUntilCleared()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);
        var now = DateTimeOffset.UtcNow;

        site.UpdateAssignmentPenalty(300, now);
        site.ClearDomainEvents();
        site.UpdateAssignmentPenalty(180, now);

        Assert.Single(site.DomainEvents);
    }

    // `23-11`'s own Done-when: "a tenant on Visible sees today's behaviour, byte for byte" - the
    // regression this default protects, the identical shape `Constructor_WhenValid_DefaultsAssignmentPenaltyToTwoMinutes`
    // above already establishes for its own column.
    [Fact]
    public void Constructor_WhenValid_DefaultsContactVisibilityToVisible()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);

        Assert.Equal(ContactVisibility.Visible, site.ContactVisibility);
    }

    [Fact]
    public void UpdateContactVisibility_WhenCalled_SetsTheValue()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);

        site.UpdateContactVisibility(ContactVisibility.MaskedWithReveal, DateTimeOffset.UtcNow);

        Assert.Equal(ContactVisibility.MaskedWithReveal, site.ContactVisibility);
    }

    [Fact]
    public void UpdateContactVisibility_WhenCalled_RaisesDomainEventExactlyOnce_CarryingTheCompleteCurrentRung()
    {
        var id = new SiteId(Guid.NewGuid());
        var site = new Site(id, "shop_7f3a", []);
        var now = DateTimeOffset.UtcNow;

        site.UpdateContactVisibility(ContactVisibility.MaskedWithReveal, now);

        var domainEvent = Assert.Single(site.DomainEvents);
        var raised = Assert.IsType<SiteContactVisibilityUpdated>(domainEvent);
        Assert.Equal(id, raised.SiteId);
        Assert.Equal("shop_7f3a", raised.PublicKey);
        Assert.Equal(ContactVisibility.MaskedWithReveal, raised.Rung);
        Assert.Equal(now, raised.OccurredAt);
    }

    [Fact]
    public void UpdateContactVisibility_WhenCalledTwice_RaisesTwoDomainEventsUntilCleared()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", []);
        var now = DateTimeOffset.UtcNow;

        site.UpdateContactVisibility(ContactVisibility.MaskedWithReveal, now);
        site.ClearDomainEvents();
        site.UpdateContactVisibility(ContactVisibility.Visible, now);

        Assert.Single(site.DomainEvents);
    }

    // `23-48`: Site's own seventh update path - the first to ever change AllowedOrigins after
    // construction. These are the domain-level fails-before: before UpdateAllowedOrigins existed,
    // nothing on this aggregate could change the value at all, so every one of these would have had
    // no method to call.

    [Fact]
    public void UpdateAllowedOrigins_WhenCalled_ReplacesTheList()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", ["https://old.example"]);

        site.UpdateAllowedOrigins(["https://new.example", "https://second.example"], DateTimeOffset.UtcNow);

        Assert.Equal(["https://new.example", "https://second.example"], site.AllowedOrigins);
    }

    [Fact]
    public void UpdateAllowedOrigins_WhenCalled_RaisesDomainEventExactlyOnce_CarryingBothThePreviousAndNewLists()
    {
        var id = new SiteId(Guid.NewGuid());
        var site = new Site(id, "shop_7f3a", ["https://old.example"]);
        var now = DateTimeOffset.UtcNow;

        site.UpdateAllowedOrigins(["https://new.example"], now);

        var domainEvent = Assert.Single(site.DomainEvents);
        var raised = Assert.IsType<SiteAllowedOriginsUpdated>(domainEvent);
        Assert.Equal(id, raised.SiteId);
        Assert.Equal("shop_7f3a", raised.PublicKey);
        Assert.Equal(["https://old.example"], raised.PreviousOrigins);
        Assert.Equal(["https://new.example"], raised.AllowedOrigins);
        Assert.Equal(now, raised.OccurredAt);
    }

    [Fact]
    public void UpdateAllowedOrigins_WhenCalledTwice_RaisesTwoDomainEventsUntilCleared()
    {
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", ["https://old.example"]);
        var now = DateTimeOffset.UtcNow;

        site.UpdateAllowedOrigins(["https://new.example"], now);
        site.ClearDomainEvents();
        site.UpdateAllowedOrigins(["https://old.example"], now);

        Assert.Single(site.DomainEvents);
    }

    [Fact]
    public void UpdateAllowedOrigins_WhenTheSameListIsSetAgain_StillRaisesAnEvent()
    {
        // No "no-op if unchanged" short-circuit - the same "the write always happens, the cache is
        // always re-invalidated" shape every other Site update path uses (re-broadcasting an
        // invalidation for an unchanged key is free, SiteCacheInvalidationConsumer's own remarks).
        var site = new Site(new SiteId(Guid.NewGuid()), "shop_7f3a", ["https://old.example"]);

        site.UpdateAllowedOrigins(["https://old.example"], DateTimeOffset.UtcNow);

        Assert.Single(site.DomainEvents);
    }
}
