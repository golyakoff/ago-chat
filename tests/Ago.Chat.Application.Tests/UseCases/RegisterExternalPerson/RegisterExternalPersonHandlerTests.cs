using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RegisterExternalPerson;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RegisterExternalPerson;

/// <summary>`adr/0184` decision 2: what the registry writes when a module announces a person it minted an
/// id for - the id is kept verbatim, the announced facts become contact details, and the person gets an
/// emoji pair exactly as a visitor who arrived through a conversation would.</summary>
public class RegisterExternalPersonHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId PersonId = new(Guid.NewGuid());
    private static readonly DateTimeOffset RegisteredAt = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_BuildsThePersonUnderTheAnnouncedId_WithPhoneAndNameAsVisitorSourcedDetails()
    {
        var store = new FakePersonRegistrationStore();
        var handler = new RegisterExternalPersonHandler(store, new FakeIdGenerator(), new FakeVisitorEmojiPairGenerator("🦊", "🍐"));

        var outcome = await handler.HandleAsync(
            new Application.UseCases.RegisterExternalPerson.RegisterExternalPerson(SiteId, PersonId, "+79990000001", "Anna", RegisteredAt),
            CancellationToken.None);

        Assert.Equal(PersonRegistrationOutcome.Created, outcome);
        var (person, details) = Assert.Single(store.Registered);

        // The module's id, verbatim - that is what makes its reference and this registry agree.
        Assert.Equal(PersonId, person.Id);
        Assert.Equal(SiteId, person.SiteId);
        Assert.Equal(RegisteredAt, person.FirstSeenAt);
        Assert.Equal("🦊", person.EmojiCreature);
        Assert.Equal("🍐", person.EmojiFood);

        Assert.Equal(2, details.Count);
        var phone = Assert.Single(details, d => d.Kind == VisitorContactDetailKind.Phone);
        Assert.Equal("+79990000001", phone.Value);
        Assert.Equal(VisitorContactDetailSource.Visitor, phone.Source);
        Assert.False(phone.Verified);
        Assert.Equal(RegisteredAt, phone.RecordedAt);
        Assert.Equal("Anna", Assert.Single(details, d => d.Kind == VisitorContactDetailKind.Name).Value);
    }

    [Fact]
    public async Task HandleAsync_WithNoName_RecordsOnlyThePhone()
    {
        var store = new FakePersonRegistrationStore();
        var handler = new RegisterExternalPersonHandler(store, new FakeIdGenerator(), new FakeVisitorEmojiPairGenerator());

        await handler.HandleAsync(
            new Application.UseCases.RegisterExternalPerson.RegisterExternalPerson(SiteId, PersonId, "+79990000001", "  ", RegisteredAt),
            CancellationToken.None);

        var (_, details) = Assert.Single(store.Registered);
        Assert.Equal(VisitorContactDetailKind.Phone, Assert.Single(details).Kind);
    }

    [Fact]
    public async Task HandleAsync_PassesTheStoresOwnOutcomeThrough()
    {
        var store = new FakePersonRegistrationStore(PersonRegistrationOutcome.AlreadyExisted);
        var handler = new RegisterExternalPersonHandler(store, new FakeIdGenerator(), new FakeVisitorEmojiPairGenerator());

        var outcome = await handler.HandleAsync(
            new Application.UseCases.RegisterExternalPerson.RegisterExternalPerson(SiteId, PersonId, "+79990000001", null, RegisteredAt),
            CancellationToken.None);

        Assert.Equal(PersonRegistrationOutcome.AlreadyExisted, outcome);
    }
}
