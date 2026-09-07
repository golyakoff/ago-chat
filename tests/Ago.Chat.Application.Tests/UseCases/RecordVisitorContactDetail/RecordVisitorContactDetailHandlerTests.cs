using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RecordVisitorContactDetail;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RecordVisitorContactDetail;

public class RecordVisitorContactDetailHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        RecordVisitorContactDetailHandler Handler, FakeVisitorContactDetailRepository ContactDetails, ConversationId ConversationId,
        FakeAcceptanceRepository Acceptances);

    private static Fixture CreateFixture(
        bool grantPermission = true, SiteId? conversationSiteId = null, Ago.Platform.Abstractions.IRateLimiter? rateLimiter = null,
        bool requireContactConsent = false)
    {
        var effectiveSiteId = conversationSiteId ?? SiteId;
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), effectiveSiteId, VisitorId, Now);
        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationSend);
        }

        var sites = new FakeSiteRepository();
        var site = new Site(effectiveSiteId, $"site_{effectiveSiteId.Value:N}", ["https://example.test"], "Test Site", Now);
        if (requireContactConsent)
        {
            site.UpdateWidgetConfig(
                new WidgetConfig(site.WidgetConfig.PrimaryColorHex, site.WidgetConfig.Position, requireContactConsent: true), Now);
        }

        sites.Seed(site);

        var acceptances = new FakeAcceptanceRepository();
        var contactDetails = new FakeVisitorContactDetailRepository();
        var handler = new RecordVisitorContactDetailHandler(
            conversations, contactDetails, sites, acceptances, permissions, rateLimiter ?? new FakeRateLimiter(),
            new ContactDetailRateLimitOptions(), new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, contactDetails, conversation.Id, acceptances);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_WhenPermitted_SavesTheDetailWithVisitorAndTimestamp()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new RecordVisitorContactDetailAsOperator(OperatorId, SiteId, fixture.ConversationId, "Phone", "+1 555 0100"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(fixture.ContactDetails.All);
        Assert.Equal(VisitorId, saved.VisitorId);
        Assert.Equal(VisitorContactDetailKind.Phone, saved.Kind);
        Assert.Equal("+1 555 0100", saved.Value);
        Assert.Equal(OperatorId, saved.RecordedByOperatorId);
        Assert.Equal(VisitorContactDetailSource.Operator, saved.Source);
        Assert.False(saved.Verified);
        Assert.Equal(Now, saved.RecordedAt);
        Assert.Equal(saved.Id.Value, result.Value.Id);
        Assert.Equal("Operator", result.Value.Source);
        Assert.False(result.Value.Verified);
        Assert.Equal(OperatorId.Value, result.Value.RecordedByOperatorId);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_OperatorWithoutPermission_ReturnsForbidden_AndSavesNothing()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new RecordVisitorContactDetailAsOperator(OperatorId, SiteId, fixture.ConversationId, "Phone", "+1 555 0100"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.ContactDetails.All);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_UnknownConversation_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new RecordVisitorContactDetailAsOperator(OperatorId, SiteId, new ConversationId(Guid.NewGuid()), "Phone", "+1 555 0100"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    /// <summary>Cross-site isolation: a conversation that is real but belongs to a *different* site
    /// than the requester's own must read exactly like no such conversation - the same info-hiding
    /// shape `RequestChannelLinkFromConsoleHandler`'s own cross-tenant guard already proves for
    /// itself. Nothing about the fact that the row exists on another tenant leaks through the error
    /// code, and no contact detail is recorded against it.</summary>
    [Fact]
    public async Task HandleAsOperatorAsync_ConversationBelongsToADifferentSite_ReturnsNotFound_AndSavesNothing()
    {
        var otherSiteId = new SiteId(Guid.NewGuid());
        var fixture = CreateFixture(conversationSiteId: otherSiteId);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new RecordVisitorContactDetailAsOperator(OperatorId, SiteId, fixture.ConversationId, "Phone", "+1 555 0100"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
        Assert.Empty(fixture.ContactDetails.All);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_EmptyValue_ReturnsInvalid()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new RecordVisitorContactDetailAsOperator(OperatorId, SiteId, fixture.ConversationId, "Phone", "   "),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.Invalid", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_UnrecognisedKind_ReturnsInvalidKind()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new RecordVisitorContactDetailAsOperator(OperatorId, SiteId, fixture.ConversationId, "Fax", "+1 555 0100"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.InvalidKind", result.Error!.Value.Code);
    }

    // ------------------------------------------------------------------------------------------
    // `23-09`: the visitor path. A genuinely different authorization shape from the operator path
    // above - no permission check, a participant comparison instead (this class's own remarks).
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsVisitorAsync_WhenParticipant_SavesAnUnverifiedVisitorSourcedDetail_WithNoOperator()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(fixture.ConversationId, VisitorId, "Phone", "+1 555 0177"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(fixture.ContactDetails.All);
        Assert.Equal(VisitorId, saved.VisitorId);
        Assert.Equal("+1 555 0177", saved.Value);
        Assert.Equal(VisitorContactDetailSource.Visitor, saved.Source);
        Assert.False(saved.Verified);
        Assert.Null(saved.RecordedByOperatorId);
        Assert.Equal("Visitor", result.Value.Source);
        Assert.False(result.Value.Verified);
        Assert.Null(result.Value.RecordedByOperatorId);
    }

    /// <summary>Done-when: "the visitor cannot write a contact detail onto a conversation that is
    /// not theirs." A real conversation, a real visitor, but not *this* visitor - the participant
    /// check must reject it, the identical shape `CreateAttachmentHandler`'s own visitor-path guard
    /// already proves for attachments.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_NotTheConversationsVisitor_ReturnsForbidden_AndSavesNothing()
    {
        var fixture = CreateFixture();
        var someoneElse = new VisitorId(Guid.NewGuid());

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(fixture.ConversationId, someoneElse, "Phone", "+1 555 0177"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.ContactDetails.All);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_UnknownConversation_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(new ConversationId(Guid.NewGuid()), VisitorId, "Phone", "+1 555 0177"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_EmptyValue_ReturnsInvalid_AndSavesNothing()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(fixture.ConversationId, VisitorId, "Phone", "   "),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.Invalid", result.Error!.Value.Code);
        Assert.Empty(fixture.ContactDetails.All);
    }

    // ------------------------------------------------------------------------------------------
    // `23-58`: the widget's own form now asks for name, phone and e-mail, all three required, from
    // *both* of its entry points. Nothing here changes on the `ago-chat` side to make that true -
    // `VisitorContactDetailKind.Email` already existed (`14-14`), and `ValidateAndTrim` already
    // refuses an empty value for any kind - these tests demonstrate both rather than assert them,
    // per the backlog item's own "a required field enforced only in the DOM is not required."
    // ------------------------------------------------------------------------------------------

    /// <summary>Done-when: "the e-mail is stored as its own kind rather than riding as `Other`."
    /// `VisitorContactDetailKind.Email` needed no new domain work for this item - it already existed
    /// and simply was not a field the widget offered before `23-58`.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_EmailKind_SavesAsItsOwnKind_NotOther()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(fixture.ConversationId, VisitorId, "Email", "visitor@example.invalid"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(fixture.ContactDetails.All);
        Assert.Equal(VisitorContactDetailKind.Email, saved.Kind);
        Assert.Equal("visitor@example.invalid", saved.Value);
        Assert.Equal("Email", result.Value.Kind);
    }

    /// <summary>Done-when: "a submission missing any of the three is refused - server-side, not
    /// only in the browser." The widget's own required-field guard sits in the DOM; this is the
    /// server-side half for the e-mail field specifically (the phone field's own equivalent,
    /// <see cref="HandleAsVisitorAsync_EmptyValue_ReturnsInvalid_AndSavesNothing"/>, already existed
    /// before this item).</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_EmptyEmail_ReturnsInvalid_AndSavesNothing()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(fixture.ConversationId, VisitorId, "Email", "   "),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.Invalid", result.Error!.Value.Code);
        Assert.Empty(fixture.ContactDetails.All);
    }

    /// <summary>The name field's own server-side half - stored as `Other` (`ui/contactCapture.ts`'s
    /// own remarks on why), required exactly like the other two since `23-58`.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_EmptyName_AsOtherKind_ReturnsInvalid_AndSavesNothing()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(fixture.ConversationId, VisitorId, "Other", "   "),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.Invalid", result.Error!.Value.Code);
        Assert.Empty(fixture.ContactDetails.All);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_UnrecognisedKind_ReturnsInvalidKind()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(fixture.ConversationId, VisitorId, "Fax", "+1 555 0177"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.InvalidKind", result.Error!.Value.Code);
    }

    /// <summary>Done-when: "a visitor cannot set the verified flag, by any request they can
    /// construct." There is no field on <c>RecordVisitorContactDetailAsVisitor</c> a caller could set
    /// to request verification at all - the strongest form of this guarantee, proven here by
    /// asserting the outcome rather than merely noting the command's own shape.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_NeverProducesAVerifiedDetail()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(fixture.ConversationId, VisitorId, "Phone", "+1 555 0177"),
            CancellationToken.None);

        Assert.All(fixture.ContactDetails.All, d => Assert.False(d.Verified));
    }

    [Fact]
    public async Task HandleAsVisitorAsync_RateLimited_ReturnsRateLimited_AndSavesNothing()
    {
        var fixture = CreateFixture(rateLimiter: new RateLimitedFakeRateLimiter(TimeSpan.FromSeconds(30)));

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(fixture.ConversationId, VisitorId, "Phone", "+1 555 0177"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Message.RateLimited", result.Error!.Value.Code);
        Assert.Empty(fixture.ContactDetails.All);
    }

    /// <summary>Proves the *site* bucket is consulted too, not just the visitor one - the same
    /// `SelectiveFakeRateLimiter` technique `CreateAttachmentHandlerTests`'s own site-bucket test
    /// uses.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_SiteRateLimited_ReturnsRateLimited()
    {
        var fixture = CreateFixture(rateLimiter: new SelectiveFakeRateLimiter("contact-detail:site:", TimeSpan.FromSeconds(45)));

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(fixture.ConversationId, VisitorId, "Phone", "+1 555 0177"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Message.RateLimited", result.Error!.Value.Code);
        Assert.Empty(fixture.ContactDetails.All);
    }

    // ------------------------------------------------------------------------------------------
    // `24-05`: the consent gate. Both paths are checked (this class's own remarks on why), and the
    // gate attaches to *this write*, never to the conversation - `ConsentGateDoesNotBlockConversationTests`
    // (same namespace) proves the second half against `SendVisitorMessageHandler`.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsVisitorAsync_SiteRequiresConsent_AndNoneRecorded_ReturnsConsentRequired_AndSavesNothing()
    {
        var fixture = CreateFixture(requireContactConsent: true);

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(fixture.ConversationId, VisitorId, "Phone", "+1 555 0177"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.ConsentRequired", result.Error!.Value.Code);
        Assert.Empty(fixture.ContactDetails.All);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_SiteRequiresConsent_AndNoneRecorded_ReturnsConsentRequired_AndSavesNothing()
    {
        var fixture = CreateFixture(requireContactConsent: true);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new RecordVisitorContactDetailAsOperator(OperatorId, SiteId, fixture.ConversationId, "Phone", "+1 555 0100"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.ConsentRequired", result.Error!.Value.Code);
        Assert.Empty(fixture.ContactDetails.All);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_SiteRequiresConsent_AndTheVisitorHasAccepted_Succeeds()
    {
        var fixture = CreateFixture(requireContactConsent: true);
        var contactKey = SiteConsentDocumentKey.For(SiteId, VisitorConsentPurpose.Contact);
        await fixture.Acceptances.SaveAsync(
            AcceptanceRecord.ForVisitor(new AcceptanceRecordId(Guid.NewGuid()), VisitorId, contactKey, "v1", Now),
            CancellationToken.None);

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(fixture.ConversationId, VisitorId, "Phone", "+1 555 0177"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(fixture.ContactDetails.All);
    }

    /// <summary>An acceptance of an *older* published version still satisfies the gate -
    /// `GetConsentRequirementHandler`'s own remarks on why a version match is not required (`adr/0114`'s
    /// open question, not yet answered either way).</summary>
    [Fact]
    public async Task HandleAsOperatorAsync_SiteRequiresConsent_AndTheVisitorAcceptedAnOlderVersion_StillSucceeds()
    {
        var fixture = CreateFixture(requireContactConsent: true);
        var contactKey = SiteConsentDocumentKey.For(SiteId, VisitorConsentPurpose.Contact);
        await fixture.Acceptances.SaveAsync(
            AcceptanceRecord.ForVisitor(new AcceptanceRecordId(Guid.NewGuid()), VisitorId, contactKey, "v1", Now.AddDays(-30)),
            CancellationToken.None);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new RecordVisitorContactDetailAsOperator(OperatorId, SiteId, fixture.ConversationId, "Phone", "+1 555 0100"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    /// <summary>An acceptance recorded against a *different* purpose (marketing) must not satisfy the
    /// contact gate - the two are separate, independently refusable controls, never one tick.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_SiteRequiresConsent_AndOnlyMarketingWasAccepted_StillReturnsConsentRequired()
    {
        var fixture = CreateFixture(requireContactConsent: true);
        var marketingKey = SiteConsentDocumentKey.For(SiteId, VisitorConsentPurpose.Marketing);
        await fixture.Acceptances.SaveAsync(
            AcceptanceRecord.ForVisitor(new AcceptanceRecordId(Guid.NewGuid()), VisitorId, marketingKey, "v1", Now),
            CancellationToken.None);

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(fixture.ConversationId, VisitorId, "Phone", "+1 555 0177"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.ConsentRequired", result.Error!.Value.Code);
    }

    /// <summary>An acceptance recorded against a *different visitor* on the same site must not
    /// satisfy this visitor's own gate.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_SiteRequiresConsent_AndADifferentVisitorAccepted_StillReturnsConsentRequired()
    {
        var fixture = CreateFixture(requireContactConsent: true);
        var contactKey = SiteConsentDocumentKey.For(SiteId, VisitorConsentPurpose.Contact);
        var someoneElse = new VisitorId(Guid.NewGuid());
        await fixture.Acceptances.SaveAsync(
            AcceptanceRecord.ForVisitor(new AcceptanceRecordId(Guid.NewGuid()), someoneElse, contactKey, "v1", Now),
            CancellationToken.None);

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(fixture.ConversationId, VisitorId, "Phone", "+1 555 0177"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.ConsentRequired", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_SiteDoesNotRequireConsent_SucceedsWithNoAcceptanceRecordAtAll()
    {
        var fixture = CreateFixture(requireContactConsent: false);

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(fixture.ConversationId, VisitorId, "Phone", "+1 555 0177"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(fixture.Acceptances.Saved);
    }
}
