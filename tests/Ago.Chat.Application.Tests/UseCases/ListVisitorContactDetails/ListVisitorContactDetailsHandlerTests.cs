using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Application.UseCases.ListVisitorContactDetails;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ListVisitorContactDetails;

public class ListVisitorContactDetailsHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private const string PublicKey = "shop_7f3a";

    private sealed record Fixture(
        ListVisitorContactDetailsHandler Handler, FakeVisitorContactDetailRepository ContactDetails, ConversationId ConversationId);

    private static Fixture CreateFixture(
        bool permitted = true, SiteId? conversationSiteId = null, ContactVisibility rung = ContactVisibility.Visible)
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), conversationSiteId ?? SiteId, VisitorId, Now);
        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);

        var contactDetails = new FakeVisitorContactDetailRepository();
        var permissions = new FakePermissionChecker();
        if (permitted)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        }

        var site = new Site(SiteId, PublicKey, []);
        site.UpdateContactVisibility(rung, Now);
        var sites = new FakeSiteRepository();
        sites.Seed(site);
        var siteConfig = new GetSiteConfigByIdHandler(sites, new FakeCache());

        var handler = new ListVisitorContactDetailsHandler(conversations, contactDetails, permissions, siteConfig);
        return new Fixture(handler, contactDetails, conversation.Id);
    }

    private static Application.UseCases.ListVisitorContactDetails.ListVisitorContactDetails Query(ConversationId conversationId) =>
        new(conversationId, OperatorId, SiteId);

    [Fact]
    public async Task HandleAsync_ListsTheVisitorsRecordedDetails_OldestFirst()
    {
        var fixture = CreateFixture();
        var second = VisitorContactDetail.Record(
            new VisitorContactDetailId(Guid.NewGuid()), VisitorId, VisitorContactDetailKind.Email, "later@example.com",
            OperatorId, Now.AddMinutes(5));
        var first = VisitorContactDetail.Record(
            new VisitorContactDetailId(Guid.NewGuid()), VisitorId, VisitorContactDetailKind.Phone, "+1 555 0100",
            OperatorId, Now);
        await fixture.ContactDetails.SaveAsync(second, CancellationToken.None);
        await fixture.ContactDetails.SaveAsync(first, CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(Query(fixture.ConversationId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Count);
        Assert.Equal(first.Id.Value, result.Value[0].Id);
        Assert.Equal(second.Id.Value, result.Value[1].Id);
    }

    [Fact]
    public async Task HandleAsync_WithoutPermission_ReturnsForbidden()
    {
        var fixture = CreateFixture(permitted: false);

        var result = await fixture.Handler.HandleAsync(Query(fixture.ConversationId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_UnknownConversation_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Query(new ConversationId(Guid.NewGuid())), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    /// <summary>`23-09`: the read DTO must distinguish the two sources and surface a visitor-supplied
    /// row's null operator id rather than defaulting or throwing - `RecordedVisitorContactDetail`'s
    /// own remarks on why a null is never rendered as a fabricated name.</summary>
    [Fact]
    public async Task HandleAsync_DistinguishesOperatorAndVisitorSourcedRows()
    {
        var fixture = CreateFixture();
        var byOperator = VisitorContactDetail.Record(
            new VisitorContactDetailId(Guid.NewGuid()), VisitorId, VisitorContactDetailKind.Phone, "+1 555 0100",
            OperatorId, Now);
        var byVisitor = VisitorContactDetail.RecordFromVisitor(
            new VisitorContactDetailId(Guid.NewGuid()), VisitorId, VisitorContactDetailKind.Phone, "+1 555 0177",
            Now.AddMinutes(1));
        await fixture.ContactDetails.SaveAsync(byOperator, CancellationToken.None);
        await fixture.ContactDetails.SaveAsync(byVisitor, CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(Query(fixture.ConversationId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var operatorRow = result.Value.Single(d => d.Id == byOperator.Id.Value);
        var visitorRow = result.Value.Single(d => d.Id == byVisitor.Id.Value);
        Assert.Equal("Operator", operatorRow.Source);
        Assert.Equal(OperatorId.Value, operatorRow.RecordedByOperatorId);
        Assert.False(operatorRow.Verified);
        Assert.Equal("Visitor", visitorRow.Source);
        Assert.Null(visitorRow.RecordedByOperatorId);
        Assert.False(visitorRow.Verified);
    }

    /// <summary>Cross-site isolation: a conversation from a different site reads like no such
    /// conversation, and its visitor's own contact details are never listed for this requester.</summary>
    [Fact]
    public async Task HandleAsync_ConversationBelongsToADifferentSite_ReturnsNotFound()
    {
        var otherSiteId = new SiteId(Guid.NewGuid());
        var fixture = CreateFixture(conversationSiteId: otherSiteId);
        var detail = VisitorContactDetail.Record(
            new VisitorContactDetailId(Guid.NewGuid()), VisitorId, VisitorContactDetailKind.Phone, "+1 555 0100", OperatorId, Now);
        await fixture.ContactDetails.SaveAsync(detail, CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(Query(fixture.ConversationId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    /// <summary>`23-11`'s own Done-when: "a tenant on Visible sees today's behaviour, byte for byte" -
    /// asserted directly, because the default rung must not change the micro case.</summary>
    [Fact]
    public async Task HandleAsync_OnVisibleRung_ReturnsTheRealValueUnmasked()
    {
        var fixture = CreateFixture(rung: ContactVisibility.Visible);
        var detail = VisitorContactDetail.Record(
            new VisitorContactDetailId(Guid.NewGuid()), VisitorId, VisitorContactDetailKind.Phone, "+1 555 0100", OperatorId, Now);
        await fixture.ContactDetails.SaveAsync(detail, CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(Query(fixture.ConversationId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var row = Assert.Single(result.Value);
        Assert.Equal("+1 555 0100", row.Value);
        Assert.False(row.Masked);
    }

    /// <summary>`23-11`'s own Done-when: "a tenant on MaskedWithReveal gets masked values from the
    /// list read, and the unmasked value is not present anywhere in the list response" - asserted by
    /// searching the whole DTO, not just its own <c>Value</c> field, for the real number.</summary>
    [Fact]
    public async Task HandleAsync_OnMaskedWithRevealRung_MasksTheValue_AndNeverReturnsTheRealOne()
    {
        var fixture = CreateFixture(rung: ContactVisibility.MaskedWithReveal);
        const string real = "+1 555 0100";
        var detail = VisitorContactDetail.Record(
            new VisitorContactDetailId(Guid.NewGuid()), VisitorId, VisitorContactDetailKind.Phone, real, OperatorId, Now);
        await fixture.ContactDetails.SaveAsync(detail, CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(Query(fixture.ConversationId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var row = Assert.Single(result.Value);
        Assert.True(row.Masked);
        Assert.NotEqual(real, row.Value);
        Assert.DoesNotContain(real, row.Value);
        // The response as a whole - not merely the one field a careless future edit might mask -
        // carries no trace of the real value.
        Assert.DoesNotContain(real, System.Text.Json.JsonSerializer.Serialize(result.Value));
    }
}
