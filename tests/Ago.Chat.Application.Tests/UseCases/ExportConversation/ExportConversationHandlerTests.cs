using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ExportConversation;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ExportConversation;

public class ExportConversationHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly SiteId OtherSiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(ExportConversationHandler Handler, FakePersonExportArchiveWriter Writer);

    private static Fixture CreateFixture(bool grantPermission = true, bool seedConversation = true, SiteId? seededSiteId = null)
    {
        var readStore = new FakeConversationReadStore();
        if (seedConversation)
        {
            var conversation = Conversation.Start(ConversationId, seededSiteId ?? SiteId, VisitorId, Now);
            readStore.Seed(conversation);
        }

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationExport);
        }

        var writer = new FakePersonExportArchiveWriter();
        var handler = new ExportConversationHandler(
            readStore, writer, new FakeRateLimiter(), permissions, new PersonExportRateLimitOptions(), new FakeClock(Now));
        return new Fixture(handler, writer);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_ReturnsTheArchive_BuiltForJustThisConversation()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ExportConversation.ExportConversation(ConversationId, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.ToString() : null);
        Assert.NotNull(result.Value.Content);
        Assert.Contains(ConversationId.Value.ToString("N"), result.Value.FileName);

        var call = Assert.Single(fixture.Writer.Calls);
        Assert.Equal(SiteId, call.SiteId);
        Assert.Equal(VisitorId, call.VisitorId);
        Assert.Equal([ConversationId], call.ConversationIds);
        Assert.Equal("conversation", call.Scope);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksConversationExport_ReturnsForbidden_AndBuildsNoArchive()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ExportConversation.ExportConversation(ConversationId, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.Writer.Calls);
    }

    [Fact]
    public async Task HandleAsync_WhenTheConversationDoesNotExist_ReturnsNotFound()
    {
        var fixture = CreateFixture(seedConversation: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ExportConversation.ExportConversation(ConversationId, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    // The same cross-tenant guard RequestConversationErasureHandlerTests proves for erasure: a
    // conversation that exists but belongs to a different site answers the identical NotFound a
    // genuinely missing one would, never Forbidden.
    [Fact]
    public async Task HandleAsync_WhenTheConversationBelongsToADifferentSite_ReturnsNotFound_NotForbidden()
    {
        var fixture = CreateFixture(seededSiteId: OtherSiteId);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.ExportConversation.ExportConversation(ConversationId, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenRateLimited_ReturnsPersonExportRateLimited_AndBuildsNoArchive()
    {
        var readStore = new FakeConversationReadStore();
        readStore.Seed(Conversation.Start(ConversationId, SiteId, VisitorId, Now));
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationExport);
        var writer = new FakePersonExportArchiveWriter();
        var retryAfter = TimeSpan.FromSeconds(42);
        var handler = new ExportConversationHandler(
            readStore, writer, new RateLimitedFakeRateLimiter(retryAfter), permissions, new PersonExportRateLimitOptions(),
            new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.ExportConversation.ExportConversation(ConversationId, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("PersonExport.RateLimited", result.Error!.Value.Code);
        Assert.Empty(writer.Calls);
    }
}
