using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RevealVisitorContactDetail;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RevealVisitorContactDetail;

/// <summary>`23-11`'s own Done-when: "a reveal returns the value and writes exactly one record naming
/// the operator"; "a caller without ConversationRead cannot reveal, and gets the same refusal the list
/// gives"; "a caller of another tenant cannot reveal (a tenant-isolation test)."</summary>
public class RevealVisitorContactDetailHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());

    private sealed record Fixture(
        RevealVisitorContactDetailHandler Handler,
        FakeContactRevealRepository Reveals,
        ConversationId ConversationId,
        VisitorContactDetailId DetailId);

    private static async Task<Fixture> CreateFixtureAsync(
        bool grantPermission = true, SiteId? conversationSiteId = null, VisitorId? detailVisitorId = null,
        string value = "+1 555 0100")
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), conversationSiteId ?? SiteId, VisitorId, Now);
        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        }

        var contactDetails = new FakeVisitorContactDetailRepository();
        var detail = VisitorContactDetail.Record(
            new VisitorContactDetailId(Guid.NewGuid()), detailVisitorId ?? VisitorId, VisitorContactDetailKind.Phone,
            value, OperatorId, Now);
        await contactDetails.SaveAsync(detail, CancellationToken.None);

        var reveals = new FakeContactRevealRepository();
        var handler = new RevealVisitorContactDetailHandler(
            conversations, contactDetails, permissions, reveals, new FakeIdGenerator(), new FakeClock(Now));
        return new Fixture(handler, reveals, conversation.Id, detail.Id);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_ReturnsTheRealValue_AndWritesExactlyOneRecord()
    {
        var fixture = await CreateFixtureAsync(value: "+1 555 0100");

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RevealVisitorContactDetail.RevealVisitorContactDetail(
                fixture.ConversationId, fixture.DetailId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("+1 555 0100", result.Value.Value);
        Assert.False(result.Value.Masked);
        var record = Assert.Single(fixture.Reveals.Recorded);
        Assert.Equal(SiteId, record.SiteId);
        Assert.Equal(fixture.ConversationId.Value, record.ConversationId);
        Assert.Equal(fixture.DetailId.Value, record.ContactDetailId);
        Assert.Equal(OperatorId, record.OperatorId);
        Assert.False(string.IsNullOrWhiteSpace(record.Surface));
        Assert.Equal(Now, record.OccurredAt);
    }

    [Fact]
    public async Task HandleAsync_OperatorWithoutPermission_ReturnsForbidden_TheSameCodeTheListGives_AndWritesNoRecord()
    {
        var fixture = await CreateFixtureAsync(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RevealVisitorContactDetail.RevealVisitorContactDetail(
                fixture.ConversationId, fixture.DetailId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.Reveals.Recorded);
    }

    [Fact]
    public async Task HandleAsync_UnknownConversation_ReturnsNotFound_AndWritesNoRecord()
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RevealVisitorContactDetail.RevealVisitorContactDetail(
                new ConversationId(Guid.NewGuid()), fixture.DetailId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
        Assert.Empty(fixture.Reveals.Recorded);
    }

    /// <summary>Tenant isolation: a conversation from a different site reads like no such
    /// conversation, and nothing is revealed or recorded for this requester.</summary>
    [Fact]
    public async Task HandleAsync_ConversationBelongsToADifferentSite_ReturnsNotFound_AndWritesNoRecord()
    {
        var otherSiteId = new SiteId(Guid.NewGuid());
        var fixture = await CreateFixtureAsync(conversationSiteId: otherSiteId);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RevealVisitorContactDetail.RevealVisitorContactDetail(
                fixture.ConversationId, fixture.DetailId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
        Assert.Empty(fixture.Reveals.Recorded);
    }

    /// <summary>The same "wrong visitor reads like no row" info-hiding guard
    /// <c>DeleteVisitorContactDetailHandler</c>'s own remarks describe.</summary>
    [Fact]
    public async Task HandleAsync_DetailBelongsToADifferentVisitor_ReturnsNotFound_AndWritesNoRecord()
    {
        var fixture = await CreateFixtureAsync(detailVisitorId: new VisitorId(Guid.NewGuid()));

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RevealVisitorContactDetail.RevealVisitorContactDetail(
                fixture.ConversationId, fixture.DetailId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.NotFound", result.Error!.Value.Code);
        Assert.Empty(fixture.Reveals.Recorded);
    }

    [Fact]
    public async Task HandleAsync_UnknownContactDetailId_ReturnsNotFound_AndWritesNoRecord()
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RevealVisitorContactDetail.RevealVisitorContactDetail(
                fixture.ConversationId, new VisitorContactDetailId(Guid.NewGuid()), OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.NotFound", result.Error!.Value.Code);
        Assert.Empty(fixture.Reveals.Recorded);
    }
}
