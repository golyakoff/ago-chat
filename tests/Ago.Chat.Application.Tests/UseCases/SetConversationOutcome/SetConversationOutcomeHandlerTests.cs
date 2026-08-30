using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.SetConversationOutcome;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.SetConversationOutcome;

public class SetConversationOutcomeHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly SiteId OtherSiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(SetConversationOutcomeHandler Handler, FakeConversationRepository Conversations, ConversationId ConversationId);

    private static Fixture CreateFixture(bool grantPermission = true, bool alsoGrantOnOtherSite = false)
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationClose);
        }

        if (alsoGrantOnOtherSite)
        {
            permissions.Grant(OperatorId, OtherSiteId, Permission.ConversationClose);
        }

        return new Fixture(new SetConversationOutcomeHandler(conversations, permissions), conversations, conversation.Id);
    }

    [Theory]
    [InlineData("Converted")]
    [InlineData("NotConverted")]
    [InlineData("FollowUpNeeded")]
    [InlineData("converted")]
    public async Task HandleAsync_WhenPermitted_RecordsTheOutcome(string wireValue)
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetConversationOutcome.SetConversationOutcome(fixture.ConversationId, SiteId, OperatorId, wireValue),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = await fixture.Conversations.GetByIdAsync(fixture.ConversationId, CancellationToken.None);
        Assert.Equal(
            Enum.Parse<ConversationOutcome>(wireValue, ignoreCase: true), saved!.Outcome);
    }

    [Fact]
    public async Task HandleAsync_CalledTwice_TheSecondCallOverwritesTheFirst()
    {
        var fixture = CreateFixture();
        var command1 = new Application.UseCases.SetConversationOutcome.SetConversationOutcome(
            fixture.ConversationId, SiteId, OperatorId, "FollowUpNeeded");
        var command2 = new Application.UseCases.SetConversationOutcome.SetConversationOutcome(
            fixture.ConversationId, SiteId, OperatorId, "Converted");

        await fixture.Handler.HandleAsync(command1, CancellationToken.None);
        var result = await fixture.Handler.HandleAsync(command2, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = await fixture.Conversations.GetByIdAsync(fixture.ConversationId, CancellationToken.None);
        Assert.Equal(ConversationOutcome.Converted, saved!.Outcome);
    }

    [Fact]
    public async Task HandleAsync_OperatorWithoutPermission_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetConversationOutcome.SetConversationOutcome(fixture.ConversationId, SiteId, OperatorId, "Converted"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_UnknownConversation_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetConversationOutcome.SetConversationOutcome(
                new ConversationId(Guid.NewGuid()), SiteId, OperatorId, "Converted"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    /// <summary>`17-01`'s own bar: a conversation that exists, but for a different site, must read
    /// exactly like one that does not exist at all - never a `Forbidden` that would confirm its
    /// existence to a caller from the wrong tenant.</summary>
    /// <summary>The operator genuinely holds `conversation:close` on the other site too (a real
    /// operator of a different tenant) - isolating this from the permission check above it, so a
    /// `Conversation.NotFound` here proves the conversation lookup itself is site-scoped, not merely
    /// that the caller lacked a grant.</summary>
    [Fact]
    public async Task HandleAsync_ConversationBelongsToADifferentSite_ReturnsNotFound()
    {
        var fixture = CreateFixture(alsoGrantOnOtherSite: true);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetConversationOutcome.SetConversationOutcome(fixture.ConversationId, OtherSiteId, OperatorId, "Converted"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    [Theory]
    [InlineData("Unset")]
    [InlineData("unset")]
    [InlineData("")]
    [InlineData("SoldIt")]
    public async Task HandleAsync_AnUnrecordableOutcome_ReturnsOutcomeInvalid(string wireValue)
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetConversationOutcome.SetConversationOutcome(fixture.ConversationId, SiteId, OperatorId, wireValue),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.OutcomeInvalid", result.Error!.Value.Code);
        var saved = await fixture.Conversations.GetByIdAsync(fixture.ConversationId, CancellationToken.None);
        Assert.Equal(ConversationOutcome.Unset, saved!.Outcome);
    }
}
