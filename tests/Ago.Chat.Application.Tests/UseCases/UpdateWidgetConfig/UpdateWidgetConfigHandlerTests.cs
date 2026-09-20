using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.UpdateWidgetConfig;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.UpdateWidgetConfig;

public class UpdateWidgetConfigHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        UpdateWidgetConfigHandler Handler, FakeSiteRepository Sites, FakeOutboxWriter Outbox);

    private static Fixture CreateFixture(bool grantPermission = true)
    {
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, "shop_7f3a", []));
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        }

        var outbox = new FakeOutboxWriter();
        var handler = new UpdateWidgetConfigHandler(
            sites, permissions, outbox, new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, sites, outbox);
    }

    private static Application.UseCases.UpdateWidgetConfig.UpdateWidgetConfig Command(
        string? primaryColorHex = null,
        string position = nameof(Position.BottomRight),
        string locale = nameof(Locale.En),
        string? noticeText = null,
        string? noticeUrl = null,
        bool attractAttention = false,
        bool autoOpenEnabled = false,
        int autoOpenDelaySeconds = 30,
        string? autoOpenGreetingText = null,
        string? contactCaptureConfirmationText = null,
        string channelSwitcherPlacement = nameof(ChannelSwitcherPlacement.AboveComposer),
        string channelSwitcherIconSize = nameof(ChannelSwitcherIconSize.Medium)) =>
        new(
            SiteId, OperatorId, primaryColorHex, position, locale, noticeText, noticeUrl,
            AttractAttention: attractAttention, AutoOpenEnabled: autoOpenEnabled,
            AutoOpenDelaySeconds: autoOpenDelaySeconds, AutoOpenGreetingText: autoOpenGreetingText,
            ContactCaptureConfirmationText: contactCaptureConfirmationText,
            ChannelSwitcherPlacement: channelSwitcherPlacement,
            ChannelSwitcherIconSize: channelSwitcherIconSize);

    [Fact]
    public async Task HandleAsync_WhenPermitted_UpdatesTheSitesWidgetConfig()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            Command("#abcdef", nameof(Position.BottomLeft)), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("#abcdef", result.Value.PrimaryColorHex);
        Assert.Equal(Position.BottomLeft, result.Value.Position);

        var saved = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.Equal("#abcdef", saved!.WidgetConfig.PrimaryColorHex);
        Assert.Equal(Position.BottomLeft, saved.WidgetConfig.Position);
    }

    // `11-10`: the third domain field this same call writes - Site.UpdateLocale runs alongside
    // Site.UpdateWidgetConfig inside this one handler invocation (its own remarks explain why one
    // HTTP call still calls two Site methods).
    [Fact]
    public async Task HandleAsync_WhenPermitted_UpdatesTheSitesLocale()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(locale: nameof(Locale.Ru)), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Locale.Ru, result.Value.Locale);

        var saved = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.Equal(Locale.Ru, saved!.Locale);
    }

    // `16-04`: the fourth and fifth fields this same call writes - straight onto
    // Ago.Chat.Domain.WidgetConfig itself, no third Site method needed (WidgetConfig's own remarks
    // explain why these two, unlike Locale, stay part of that value object).
    [Fact]
    public async Task HandleAsync_WhenPermitted_UpdatesTheSitesNoticeTextAndUrl()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            Command(noticeText: "We read what you send us.", noticeUrl: "https://tenant.example/privacy"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("We read what you send us.", result.Value.NoticeText);
        Assert.Equal("https://tenant.example/privacy", result.Value.NoticeUrl);

        var saved = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.Equal("We read what you send us.", saved!.WidgetConfig.NoticeText);
        Assert.Equal("https://tenant.example/privacy", saved.WidgetConfig.NoticeUrl);
    }

    // `16-04`'s own Scope - both fields must be leaveable empty, and a tenant leaving them empty gets
    // no notice, not an AGO-authored one.
    // `23-63`: the sixth field this same call writes - straight onto WidgetConfig itself, the
    // identical "no third Site method needed" shape NoticeText/NoticeUrl already established.
    [Fact]
    public async Task HandleAsync_WhenPermitted_UpdatesTheSitesAttractAttention()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(attractAttention: true), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.AttractAttention);

        var saved = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.True(saved!.WidgetConfig.AttractAttention);
    }

    // `23-63`'s own Decision: "a setting, off unless the tenant turns it on" - the default this
    // handler must never silently flip.
    [Fact]
    public async Task HandleAsync_WhenAttractAttentionIsNotSupplied_LeavesItFalse()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.AttractAttention);
    }

    [Fact]
    public async Task HandleAsync_WhenNoticeFieldsAreNotSupplied_LeavesThemNull()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.NoticeText);
        Assert.Null(result.Value.NoticeUrl);
    }

    // `11-10`: was "...EnqueuesExactlyOneSiteSettingsChangedEnvelope" before this item - the handler
    // now calls two Site methods (UpdateWidgetConfig, UpdateLocale) per request, each raising its own
    // domain event, each mapped to SiteSettingsChanged and enqueued - so a single successful call now
    // enqueues two envelopes of that same type, not one. SiteCacheInvalidationConsumer treats a
    // repeat invalidation of the same key as free (its own remarks), so two envelopes cost nothing
    // beyond this. `16-04`'s two new fields ride the existing UpdateWidgetConfig call and its existing
    // SiteWidgetConfigUpdated event, so this count is unchanged by this item - stated explicitly so a
    // later session does not assume a third envelope appeared.
    [Fact]
    public async Task HandleAsync_WhenPermitted_EnqueuesTwoSiteSettingsChangedEnvelopes()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, fixture.Outbox.Enqueued.Count);
        Assert.All(fixture.Outbox.Enqueued, envelope => Assert.Equal(nameof(SiteSettingsChanged), envelope.Type));
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_ClearsTheSitesDomainEventsAfterEnqueuing()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(Command(position: nameof(Position.BottomLeft)), CancellationToken.None);

        var saved = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.Empty(saved!.DomainEvents);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksSiteConfigure_ReturnsForbidden_AndEnqueuesNothing()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            Command("#abcdef", nameof(Position.BottomLeft)), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_WhenTheSiteDoesNotExist_ReturnsSiteNotFound()
    {
        var sites = new FakeSiteRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        var handler = new UpdateWidgetConfigHandler(
            sites, permissions, new FakeOutboxWriter(), new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            Command("#abcdef", nameof(Position.BottomLeft)), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.NotFound", result.Error!.Value.Code);
    }

    [Theory]
    [InlineData("blue")]
    [InlineData("#fff")]
    [InlineData("#gggggg")]
    public async Task HandleAsync_WhenTheColorIsMalformedHex_ReturnsInvalidColor(string malformedHex)
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            Command(malformedHex, nameof(Position.BottomLeft)), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("WidgetConfig.InvalidColor", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenThePositionIsNotARecognizedValue_ReturnsInvalidPosition()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(position: "diagonal"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("WidgetConfig.InvalidPosition", result.Error!.Value.Code);
    }

    // `11-10`'s own guard, mirroring the Position test just above.
    [Fact]
    public async Task HandleAsync_WhenTheLocaleIsNotARecognizedValue_ReturnsInvalidLocale()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(locale: "klingon"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("WidgetConfig.InvalidLocale", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheLocaleIsInvalid_EnqueuesNothing()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(Command(locale: "klingon"), CancellationToken.None);

        Assert.Empty(fixture.Outbox.Enqueued);
    }

    // `16-04`: mirrors the color/position/locale guards above - a bad notice URL is a clean `Result`
    // failure with its own error code, not an unhandled exception, and it costs no outbox writes.
    [Theory]
    [InlineData("http://tenant.example/privacy")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    public async Task HandleAsync_WhenTheNoticeUrlIsNotAbsoluteHttps_ReturnsInvalidNoticeUrl(string malformedUrl)
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(noticeUrl: malformedUrl), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("WidgetConfig.InvalidNoticeUrl", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheNoticeUrlIsInvalid_EnqueuesNothing()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(Command(noticeUrl: "http://tenant.example/privacy"), CancellationToken.None);

        Assert.Empty(fixture.Outbox.Enqueued);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleAsync_WhenTheNoticeTextIsWhitespaceOnly_ReturnsInvalidNoticeText(string malformedText)
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(noticeText: malformedText), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("WidgetConfig.InvalidNoticeText", result.Error!.Value.Code);
    }

    // `23-64`: the seventh, eighth and ninth fields this same call writes - straight onto
    // Ago.Chat.Domain.WidgetConfig itself, the identical "no third Site method needed" shape
    // NoticeText/NoticeUrl/AttractAttention already established.
    [Fact]
    public async Task HandleAsync_WhenPermitted_UpdatesTheSitesAutoOpenFields()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            Command(autoOpenEnabled: true, autoOpenDelaySeconds: 60, autoOpenGreetingText: "Hi, need any help?"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.AutoOpenEnabled);
        Assert.Equal(AutoOpenDelay.Seconds60, result.Value.AutoOpenDelaySeconds);
        Assert.Equal("Hi, need any help?", result.Value.AutoOpenGreetingText);

        var saved = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.True(saved!.WidgetConfig.AutoOpenEnabled);
        Assert.Equal(AutoOpenDelay.Seconds60, saved.WidgetConfig.AutoOpenDelaySeconds);
        Assert.Equal("Hi, need any help?", saved.WidgetConfig.AutoOpenGreetingText);
    }

    // `23-64`'s own Decision: "off by default" - the same default `AttractAttention`'s own equivalent
    // test already guards, restated for this item's own field.
    [Fact]
    public async Task HandleAsync_WhenAutoOpenIsNotSupplied_LeavesItDisabled_WithTheDefaultDelay()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.AutoOpenEnabled);
        Assert.Equal(AutoOpenDelay.Seconds30, result.Value.AutoOpenDelaySeconds);
        Assert.Null(result.Value.AutoOpenGreetingText);
    }

    // `23-64`'s own Scope: "15, 30, 45, 60, 90, 120 seconds" - a closed set, not a free number.
    [Fact]
    public async Task HandleAsync_WhenTheAutoOpenDelayIsNotOneOfTheSixValues_ReturnsInvalidAutoOpenDelay()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            Command(autoOpenDelaySeconds: 20), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("WidgetConfig.InvalidAutoOpenDelay", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheAutoOpenDelayIsInvalid_EnqueuesNothing()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(Command(autoOpenDelaySeconds: 20), CancellationToken.None);

        Assert.Empty(fixture.Outbox.Enqueued);
    }

    // `23-64`'s own Scope: "There is no default sentence we supply" - turning auto-open on with
    // nothing to say is a rejection, not a silently-broken panel.
    [Fact]
    public async Task HandleAsync_WhenAutoOpenIsEnabledWithNoGreetingText_ReturnsInvalidAutoOpenGreetingText()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            Command(autoOpenEnabled: true, autoOpenGreetingText: null), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("WidgetConfig.InvalidAutoOpenGreetingText", result.Error!.Value.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleAsync_WhenTheAutoOpenGreetingTextIsWhitespaceOnly_ReturnsInvalidAutoOpenGreetingText(
        string malformedText)
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            Command(autoOpenEnabled: true, autoOpenGreetingText: malformedText), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("WidgetConfig.InvalidAutoOpenGreetingText", result.Error!.Value.Code);
    }

    // `25-129`: the tenth field this same call writes - straight onto Ago.Chat.Domain.WidgetConfig
    // itself, the identical "no third Site method needed" shape NoticeText/AutoOpenGreetingText
    // already established. Unlike AutoOpenGreetingText, there is no enabling flag to pair it with -
    // this text has no "on/off" of its own, the contact-capture control always exists.
    [Fact]
    public async Task HandleAsync_WhenPermitted_UpdatesTheSitesContactCaptureConfirmationText()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            Command(contactCaptureConfirmationText: "Спасибо, {name}, мы всё записали."), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Спасибо, {name}, мы всё записали.", result.Value.ContactCaptureConfirmationText);

        var saved = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.Equal("Спасибо, {name}, мы всё записали.", saved!.WidgetConfig.ContactCaptureConfirmationText);
    }

    // A tenant who has not configured one gets null - the widget's own default sentence is what fills
    // that in, not a value this handler invents.
    [Fact]
    public async Task HandleAsync_WhenContactCaptureConfirmationTextIsNotSupplied_LeavesItNull()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.ContactCaptureConfirmationText);
    }

    // Mirrors the notice-text guard above - a bad confirmation text is a clean Result failure with its
    // own error code, not an unhandled exception, and it costs no outbox writes.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleAsync_WhenTheContactCaptureConfirmationTextIsWhitespaceOnly_ReturnsInvalidContactCaptureConfirmationText(
        string malformedText)
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            Command(contactCaptureConfirmationText: malformedText), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("WidgetConfig.InvalidContactCaptureConfirmationText", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheContactCaptureConfirmationTextExceedsMaxLength_ReturnsInvalidContactCaptureConfirmationText()
    {
        var fixture = CreateFixture();
        var tooLong = new string('a', WidgetConfig.MaxContactCaptureConfirmationTextLength + 1);

        var result = await fixture.Handler.HandleAsync(
            Command(contactCaptureConfirmationText: tooLong), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("WidgetConfig.InvalidContactCaptureConfirmationText", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheContactCaptureConfirmationTextIsInvalid_EnqueuesNothing()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(Command(contactCaptureConfirmationText: "   "), CancellationToken.None);

        Assert.Empty(fixture.Outbox.Enqueued);
    }

    // `25-173`: the eleventh and twelfth fields this same call writes - straight onto
    // Ago.Chat.Domain.WidgetConfig itself, the identical "no third Site method needed" shape every
    // field above already established.
    [Fact]
    public async Task HandleAsync_WhenPermitted_UpdatesTheSitesChannelSwitcherFields()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            Command(
                channelSwitcherPlacement: nameof(ChannelSwitcherPlacement.BelowLauncher),
                channelSwitcherIconSize: nameof(ChannelSwitcherIconSize.Small)),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ChannelSwitcherPlacement.BelowLauncher, result.Value.ChannelSwitcherPlacement);
        Assert.Equal(ChannelSwitcherIconSize.Small, result.Value.ChannelSwitcherIconSize);

        var saved = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.Equal(ChannelSwitcherPlacement.BelowLauncher, saved!.WidgetConfig.ChannelSwitcherPlacement);
        Assert.Equal(ChannelSwitcherIconSize.Small, saved.WidgetConfig.ChannelSwitcherIconSize);
    }

    // The backlog's own default - `AboveComposer`/`Medium` - is what every existing site must keep
    // reading when this item's own fields are never sent, `25-149`'s pre-existing card unchanged.
    [Fact]
    public async Task HandleAsync_WhenChannelSwitcherFieldsAreNotSupplied_LeavesTheDefaultPlacementAndSize()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ChannelSwitcherPlacement.AboveComposer, result.Value.ChannelSwitcherPlacement);
        Assert.Equal(ChannelSwitcherIconSize.Medium, result.Value.ChannelSwitcherIconSize);
    }

    [Fact]
    public async Task HandleAsync_WhenTheChannelSwitcherPlacementIsNotARecognizedValue_ReturnsInvalidChannelSwitcherPlacement()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            Command(channelSwitcherPlacement: "floating"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("WidgetConfig.InvalidChannelSwitcherPlacement", result.Error!.Value.Code);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_WhenTheChannelSwitcherIconSizeIsNotARecognizedValue_ReturnsInvalidChannelSwitcherIconSize()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            Command(channelSwitcherIconSize: "huge"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("WidgetConfig.InvalidChannelSwitcherIconSize", result.Error!.Value.Code);
        Assert.Empty(fixture.Outbox.Enqueued);
    }
}
