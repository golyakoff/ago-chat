using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ExportConversation;
using Ago.Chat.Application.UseCases.ExportVisitor;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ExportVisitor;

public class ExportVisitorHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());
    private static readonly ConversationId OtherConversationId = new(Guid.NewGuid());
    private static readonly ConversationId StrangerConversationId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // `24-11`'s own point: a visitor-scoped export spans every conversation the same visitor has, and
    // no conversation belonging to a different visitor - proven here at the handler level (the same
    // scope the archive writer itself is proven against with a real Postgres in
    // PersonExportIntegrationTests).
    [Fact]
    public async Task HandleAsync_WhenPermitted_BuildsTheArchive_ForEveryConversationOfTheSameVisitor_AndNoOthers()
    {
        var readStore = new FakeConversationReadStore();
        readStore.Seed(Conversation.Start(ConversationId, SiteId, VisitorId, Now));
        readStore.Seed(Conversation.Start(OtherConversationId, SiteId, VisitorId, Now.AddMinutes(5)));
        // A stranger's own conversation, same site, different visitor - must never appear.
        readStore.Seed(Conversation.Start(StrangerConversationId, SiteId, new VisitorId(Guid.NewGuid()), Now));

        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationExport);
        var writer = new FakePersonExportArchiveWriter();
        var handler = new ExportVisitorHandler(
            readStore, writer, new FakeRateLimiter(), permissions, new PersonExportRateLimitOptions(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.ExportVisitor.ExportVisitor(ConversationId, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.ToString() : null);

        var call = Assert.Single(writer.Calls);
        Assert.Equal(VisitorId, call.VisitorId);
        Assert.Equal("visitor", call.Scope);
        Assert.Equal(
            new[] { ConversationId, OtherConversationId }.OrderBy(id => id.Value).ToList(),
            call.ConversationIds.OrderBy(id => id.Value).ToList());
        Assert.DoesNotContain(StrangerConversationId, call.ConversationIds);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksConversationExport_ReturnsForbidden()
    {
        var readStore = new FakeConversationReadStore();
        readStore.Seed(Conversation.Start(ConversationId, SiteId, VisitorId, Now));
        var handler = new ExportVisitorHandler(
            readStore, new FakePersonExportArchiveWriter(), new FakeRateLimiter(), new FakePermissionChecker(),
            new PersonExportRateLimitOptions(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.ExportVisitor.ExportVisitor(ConversationId, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheConversationDoesNotExist_ReturnsNotFound()
    {
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationExport);
        var handler = new ExportVisitorHandler(
            new FakeConversationReadStore(), new FakePersonExportArchiveWriter(), new FakeRateLimiter(), permissions,
            new PersonExportRateLimitOptions(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.ExportVisitor.ExportVisitor(ConversationId, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }
}
