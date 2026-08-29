using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetConversationNotes;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetConversationNotes;

/// <summary>`18-04`: not the leak-proof test (that one is
/// `Ago.Chat.Integration.Tests.NoteLeakProofTests`, against the real Postgres-backed store) - this is
/// the ordinary access-check/happy-path coverage every other handler test gives itself.</summary>
public class GetConversationNotesHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(GetConversationNotesHandler Handler, FakeNoteRepository Notes, ConversationId ConversationId);

    private static Fixture CreateFixture(bool grantPermission = true)
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        var readStore = new FakeConversationReadStore();
        readStore.Seed(conversation);

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        }

        var notes = new FakeNoteRepository();
        var handler = new GetConversationNotesHandler(readStore, notes, permissions);

        return new Fixture(handler, notes, conversation.Id);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_ReturnsEveryNoteOldestFirst()
    {
        var fixture = CreateFixture();
        await fixture.Notes.SaveAsync(
            ConversationNote.Write(new ConversationNoteId(Guid.NewGuid()), fixture.ConversationId, OperatorId, "first", Now),
            CancellationToken.None);
        await fixture.Notes.SaveAsync(
            ConversationNote.Write(new ConversationNoteId(Guid.NewGuid()), fixture.ConversationId, OperatorId, "second", Now.AddMinutes(1)),
            CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetConversationNotes.GetConversationNotes(fixture.ConversationId, SiteId, OperatorId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["first", "second"], result.Value.Select(n => n.Body));
    }

    [Fact]
    public async Task HandleAsync_OperatorWithoutPermission_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetConversationNotes.GetConversationNotes(fixture.ConversationId, SiteId, OperatorId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_UnknownConversation_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetConversationNotes.GetConversationNotes(new ConversationId(Guid.NewGuid()), SiteId, OperatorId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }
}
