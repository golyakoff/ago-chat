using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.AddConversationNote;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.AddConversationNote;

public class AddConversationNoteHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(AddConversationNoteHandler Handler, FakeNoteRepository Notes, ConversationId ConversationId);

    private static Fixture CreateFixture(bool grantPermission = true)
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        var readStore = new FakeConversationReadStore();
        readStore.Seed(conversation);

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationNoteWrite);
        }

        var notes = new FakeNoteRepository();
        var handler = new AddConversationNoteHandler(
            readStore, notes, permissions, new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, notes, conversation.Id);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_SavesTheNoteWithAuthorAndTimestamp()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.AddConversationNote.AddConversationNote(
                fixture.ConversationId, SiteId, OperatorId, "Called back, wants a refund."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(fixture.Notes.Saved);
        Assert.Equal(fixture.ConversationId, saved.ConversationId);
        Assert.Equal(OperatorId, saved.AuthorId);
        Assert.Equal("Called back, wants a refund.", saved.Body);
        Assert.Equal(Now, saved.CreatedAt);
    }

    [Fact]
    public async Task HandleAsync_OperatorWithoutPermission_ReturnsForbidden_AndSavesNothing()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.AddConversationNote.AddConversationNote(fixture.ConversationId, SiteId, OperatorId, "note"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.Notes.Saved);
    }

    [Fact]
    public async Task HandleAsync_UnknownConversation_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.AddConversationNote.AddConversationNote(
                new ConversationId(Guid.NewGuid()), SiteId, OperatorId, "note"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_EmptyBody_ReturnsNoteInvalid()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.AddConversationNote.AddConversationNote(fixture.ConversationId, SiteId, OperatorId, "   "),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ConversationNote.Invalid", result.Error!.Value.Code);
    }
}
