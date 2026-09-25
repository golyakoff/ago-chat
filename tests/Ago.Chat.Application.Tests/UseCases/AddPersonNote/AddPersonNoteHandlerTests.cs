using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.AddPersonNote;
using Ago.Chat.Application.UseCases.GetPersonNotes;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.AddPersonNote;

/// <summary>`adr/0184` (O3): the operator's note about a person lives on the registry - written and read
/// back by person id, gated exactly like a conversation note, and tenant-isolated by the person's own
/// account.</summary>
public class AddPersonNoteHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly SiteId OtherSiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        AddPersonNoteHandler Add, GetPersonNotesHandler Get, FakePersonNoteRepository Notes, Visitor Person, Visitor Stranger);

    private static Fixture CreateFixture(bool grantWrite = true, bool grantRead = true)
    {
        var visitors = new FakeVisitorRepository();
        var person = new Visitor(new VisitorId(Guid.NewGuid()), SiteId, Now);
        var stranger = new Visitor(new VisitorId(Guid.NewGuid()), OtherSiteId, Now);
        visitors.Seed(person);
        visitors.Seed(stranger);

        var permissions = new FakePermissionChecker();
        if (grantWrite)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationNoteWrite);
        }

        if (grantRead)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        }

        var notes = new FakePersonNoteRepository();
        return new Fixture(
            new AddPersonNoteHandler(visitors, notes, permissions, new FakeIdGenerator(), new FakeClock(Now)),
            new GetPersonNotesHandler(visitors, notes, permissions),
            notes, person, stranger);
    }

    [Fact]
    public async Task Add_WhenPermitted_SavesTheNoteAgainstThePerson_AndGetReadsItBackOldestFirst()
    {
        var fixture = CreateFixture();

        var first = await fixture.Add.HandleAsync(
            new Application.UseCases.AddPersonNote.AddPersonNote(fixture.Person.Id, SiteId, OperatorId, "  pays late  "),
            CancellationToken.None);
        var second = await fixture.Add.HandleAsync(
            new Application.UseCases.AddPersonNote.AddPersonNote(fixture.Person.Id, SiteId, OperatorId, "prefers afternoons"),
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Equal("pays late", first.Value.Body);
        Assert.Equal(fixture.Person.Id.Value, first.Value.PersonId);
        Assert.Equal(OperatorId.Value, first.Value.AuthorId);
        Assert.Equal(Now, first.Value.CreatedAt);

        var read = await fixture.Get.HandleAsync(
            new Application.UseCases.GetPersonNotes.GetPersonNotes(fixture.Person.Id, SiteId, OperatorId), CancellationToken.None);

        Assert.True(read.IsSuccess, read.Error?.Message);
        Assert.Equal(["pays late", "prefers afternoons"], read.Value.Select(n => n.Body));
    }

    [Fact]
    public async Task Add_WithoutNoteWrite_IsForbidden_AndSavesNothing()
    {
        var fixture = CreateFixture(grantWrite: false);

        var result = await fixture.Add.HandleAsync(
            new Application.UseCases.AddPersonNote.AddPersonNote(fixture.Person.Id, SiteId, OperatorId, "pays late"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.Notes.Saved);
    }

    /// <summary>Another account's person reads like no such person - never forbidden, which would
    /// confirm the id is real somewhere.</summary>
    [Fact]
    public async Task Add_AndGet_OnAnotherAccountsPerson_ReadLikeNotFound()
    {
        var fixture = CreateFixture();

        var added = await fixture.Add.HandleAsync(
            new Application.UseCases.AddPersonNote.AddPersonNote(fixture.Stranger.Id, SiteId, OperatorId, "pays late"),
            CancellationToken.None);
        var read = await fixture.Get.HandleAsync(
            new Application.UseCases.GetPersonNotes.GetPersonNotes(fixture.Stranger.Id, SiteId, OperatorId), CancellationToken.None);

        Assert.Equal("Person.NotFound", added.Error!.Value.Code);
        Assert.Equal("Person.NotFound", read.Error!.Value.Code);
        Assert.Empty(fixture.Notes.Saved);
    }

    [Fact]
    public async Task Add_ABlankNote_IsRefusedAsInvalid()
    {
        var fixture = CreateFixture();

        var result = await fixture.Add.HandleAsync(
            new Application.UseCases.AddPersonNote.AddPersonNote(fixture.Person.Id, SiteId, OperatorId, "   "),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ConversationNote.Invalid", result.Error!.Value.Code);
        Assert.Empty(fixture.Notes.Saved);
    }

    [Fact]
    public async Task Get_WithoutRead_IsForbidden()
    {
        var fixture = CreateFixture(grantRead: false);

        var read = await fixture.Get.HandleAsync(
            new Application.UseCases.GetPersonNotes.GetPersonNotes(fixture.Person.Id, SiteId, OperatorId), CancellationToken.None);

        Assert.True(read.IsFailure);
        Assert.Equal("Conversation.Forbidden", read.Error!.Value.Code);
    }
}
