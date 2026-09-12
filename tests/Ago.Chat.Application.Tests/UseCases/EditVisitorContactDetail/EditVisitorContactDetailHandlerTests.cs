using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.EditVisitorContactDetail;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.EditVisitorContactDetail;

public class EditVisitorContactDetailHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());

    private sealed record Fixture(
        EditVisitorContactDetailHandler Handler,
        FakeVisitorContactDetailRepository ContactDetails,
        ConversationId ConversationId,
        VisitorContactDetailId DetailId);

    private static async Task<Fixture> CreateFixtureAsync(
        bool grantPermission = true,
        SiteId? conversationSiteId = null,
        VisitorId? detailVisitorId = null,
        VisitorContactDetailKind kind = VisitorContactDetailKind.Phone,
        VisitorContactDetailSource source = VisitorContactDetailSource.Operator)
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), conversationSiteId ?? SiteId, VisitorId, Now);
        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationSend);
        }

        var contactDetails = new FakeVisitorContactDetailRepository();
        var detail = source == VisitorContactDetailSource.Visitor
            ? VisitorContactDetail.RecordFromVisitor(
                new VisitorContactDetailId(Guid.NewGuid()), detailVisitorId ?? VisitorId, kind, "+1 555 0100", Now)
            : VisitorContactDetail.Record(
                new VisitorContactDetailId(Guid.NewGuid()), detailVisitorId ?? VisitorId, kind, "+1 555 0100", OperatorId, Now);
        await contactDetails.SaveAsync(detail, CancellationToken.None);

        var handler = new EditVisitorContactDetailHandler(conversations, contactDetails, permissions);
        return new Fixture(handler, contactDetails, conversation.Id, detail.Id);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_UpdatesTheValue()
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.EditVisitorContactDetail.EditVisitorContactDetail(
                OperatorId, SiteId, fixture.ConversationId, fixture.DetailId, "+1 555 0199"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("+1 555 0199", result.Value.Value);
        Assert.Equal("+1 555 0199", fixture.ContactDetails.All.Single().Value);
    }

    /// <summary>The backlog item's own explicit warning: editing changes the existing row, not the
    /// source.</summary>
    [Fact]
    public async Task HandleAsync_TheDetailWasSubmittedByTheVisitor_EditsIt_ButKeepsSourceVisitor()
    {
        var fixture = await CreateFixtureAsync(source: VisitorContactDetailSource.Visitor);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.EditVisitorContactDetail.EditVisitorContactDetail(
                OperatorId, SiteId, fixture.ConversationId, fixture.DetailId, "+1 555 0188"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Visitor", result.Value.Source);
        Assert.Null(result.Value.RecordedByOperatorId);
        var stored = fixture.ContactDetails.All.Single();
        Assert.Equal(VisitorContactDetailSource.Visitor, stored.Source);
        Assert.Null(stored.RecordedByOperatorId);
    }

    [Fact]
    public async Task HandleAsync_ResetsAnAlreadyConfirmedAssessmentBackToUnset()
    {
        var fixture = await CreateFixtureAsync();
        fixture.ContactDetails.All.Single().SetAssessment(VisitorContactDetailAssessment.Confirmed);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.EditVisitorContactDetail.EditVisitorContactDetail(
                OperatorId, SiteId, fixture.ConversationId, fixture.DetailId, "+1 555 0199"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Unset", result.Value.Assessment);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleAsync_EmptyValue_ReturnsInvalid_AndKeepsTheOriginal(string value)
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.EditVisitorContactDetail.EditVisitorContactDetail(
                OperatorId, SiteId, fixture.ConversationId, fixture.DetailId, value),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.Invalid", result.Error!.Value.Code);
        Assert.Equal("+1 555 0100", fixture.ContactDetails.All.Single().Value);
    }

    [Fact]
    public async Task HandleAsync_OperatorWithoutPermission_ReturnsForbidden_AndKeepsTheOriginal()
    {
        var fixture = await CreateFixtureAsync(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.EditVisitorContactDetail.EditVisitorContactDetail(
                OperatorId, SiteId, fixture.ConversationId, fixture.DetailId, "+1 555 0199"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Equal("+1 555 0100", fixture.ContactDetails.All.Single().Value);
    }

    [Fact]
    public async Task HandleAsync_UnknownConversation_ReturnsNotFound()
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.EditVisitorContactDetail.EditVisitorContactDetail(
                OperatorId, SiteId, new ConversationId(Guid.NewGuid()), fixture.DetailId, "+1 555 0199"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_ConversationBelongsToADifferentSite_ReturnsNotFound()
    {
        var otherSiteId = new SiteId(Guid.NewGuid());
        var fixture = await CreateFixtureAsync(conversationSiteId: otherSiteId);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.EditVisitorContactDetail.EditVisitorContactDetail(
                OperatorId, SiteId, fixture.ConversationId, fixture.DetailId, "+1 555 0199"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_DetailBelongsToADifferentVisitor_ReturnsNotFound()
    {
        var fixture = await CreateFixtureAsync(detailVisitorId: new VisitorId(Guid.NewGuid()));

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.EditVisitorContactDetail.EditVisitorContactDetail(
                OperatorId, SiteId, fixture.ConversationId, fixture.DetailId, "+1 555 0199"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_UnknownContactDetailId_ReturnsNotFound()
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.EditVisitorContactDetail.EditVisitorContactDetail(
                OperatorId, SiteId, fixture.ConversationId, new VisitorContactDetailId(Guid.NewGuid()), "+1 555 0199"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.NotFound", result.Error!.Value.Code);
    }
}
