using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.SetVisitorContactDetailAssessment;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.SetVisitorContactDetailAssessment;

public class SetVisitorContactDetailAssessmentHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());

    private sealed record Fixture(
        SetVisitorContactDetailAssessmentHandler Handler,
        FakeVisitorContactDetailRepository ContactDetails,
        ConversationId ConversationId,
        VisitorContactDetailId DetailId);

    private static async Task<Fixture> CreateFixtureAsync(
        bool grantPermission = true,
        SiteId? conversationSiteId = null,
        VisitorId? detailVisitorId = null,
        VisitorContactDetailKind kind = VisitorContactDetailKind.Phone)
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
        var detail = VisitorContactDetail.Record(
            new VisitorContactDetailId(Guid.NewGuid()), detailVisitorId ?? VisitorId, kind, "+1 555 0100", OperatorId, Now);
        await contactDetails.SaveAsync(detail, CancellationToken.None);

        var handler = new SetVisitorContactDetailAssessmentHandler(conversations, contactDetails, permissions);
        return new Fixture(handler, contactDetails, conversation.Id, detail.Id);
    }

    [Theory]
    [InlineData("Confirmed")]
    [InlineData("Invalid")]
    public async Task HandleAsync_OnPhone_WhenPermitted_SetsTheAssessment(string assessment)
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetVisitorContactDetailAssessment.SetVisitorContactDetailAssessment(
                OperatorId, SiteId, fixture.ConversationId, fixture.DetailId, assessment),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(assessment, result.Value.Assessment);
        Assert.Equal(Enum.Parse<VisitorContactDetailAssessment>(assessment), fixture.ContactDetails.All.Single().Assessment);
    }

    [Fact]
    public async Task HandleAsync_OnEmail_WhenPermitted_SetsTheAssessment()
    {
        var fixture = await CreateFixtureAsync(kind: VisitorContactDetailKind.Email);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetVisitorContactDetailAssessment.SetVisitorContactDetailAssessment(
                OperatorId, SiteId, fixture.ConversationId, fixture.DetailId, "Confirmed"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Confirmed", result.Value.Assessment);
    }

    /// <summary>The backlog item's own decision: a name has no verifiable channel, so `Other` never
    /// gets this action - rejected as a normal, expected validation error, never reaching the domain's
    /// own defence-in-depth throw.</summary>
    [Fact]
    public async Task HandleAsync_OnOther_ReturnsAssessmentNotApplicable_AndLeavesItUnset()
    {
        var fixture = await CreateFixtureAsync(kind: VisitorContactDetailKind.Other);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetVisitorContactDetailAssessment.SetVisitorContactDetailAssessment(
                OperatorId, SiteId, fixture.ConversationId, fixture.DetailId, "Confirmed"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.AssessmentNotApplicable", result.Error!.Value.Code);
        Assert.Equal(VisitorContactDetailAssessment.Unset, fixture.ContactDetails.All.Single().Assessment);
    }

    [Theory]
    [InlineData("Unset")]
    [InlineData("NotARealValue")]
    [InlineData("")]
    public async Task HandleAsync_InvalidOrUnsettableAssessment_ReturnsInvalidAssessment(string assessment)
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetVisitorContactDetailAssessment.SetVisitorContactDetailAssessment(
                OperatorId, SiteId, fixture.ConversationId, fixture.DetailId, assessment),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.InvalidAssessment", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_OperatorWithoutPermission_ReturnsForbidden_AndLeavesItUnset()
    {
        var fixture = await CreateFixtureAsync(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetVisitorContactDetailAssessment.SetVisitorContactDetailAssessment(
                OperatorId, SiteId, fixture.ConversationId, fixture.DetailId, "Confirmed"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Equal(VisitorContactDetailAssessment.Unset, fixture.ContactDetails.All.Single().Assessment);
    }

    [Fact]
    public async Task HandleAsync_UnknownConversation_ReturnsNotFound()
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetVisitorContactDetailAssessment.SetVisitorContactDetailAssessment(
                OperatorId, SiteId, new ConversationId(Guid.NewGuid()), fixture.DetailId, "Confirmed"),
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
            new Application.UseCases.SetVisitorContactDetailAssessment.SetVisitorContactDetailAssessment(
                OperatorId, SiteId, fixture.ConversationId, fixture.DetailId, "Confirmed"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_DetailBelongsToADifferentVisitor_ReturnsNotFound()
    {
        var fixture = await CreateFixtureAsync(detailVisitorId: new VisitorId(Guid.NewGuid()));

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetVisitorContactDetailAssessment.SetVisitorContactDetailAssessment(
                OperatorId, SiteId, fixture.ConversationId, fixture.DetailId, "Confirmed"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_UnknownContactDetailId_ReturnsNotFound()
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetVisitorContactDetailAssessment.SetVisitorContactDetailAssessment(
                OperatorId, SiteId, fixture.ConversationId, new VisitorContactDetailId(Guid.NewGuid()), "Confirmed"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.NotFound", result.Error!.Value.Code);
    }
}
