using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ErasePerson;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ErasePerson;

/// <summary>`adr/0189`/`26-275` slice #3: the registry's half of "the calendar erased its own half of a
/// person and told chat to erase the rest" - a pass-through onto <see cref="IPersonErasureStore"/>, so
/// these tests exist only to prove the command's fields reach the store unchanged and the store's own
/// outcome reaches the caller unchanged; the store's real idempotency and cascade are proven against
/// Postgres in <c>PersonErasureIntegrationTests</c>.</summary>
public class ErasePersonHandlerTests
{
    private static readonly SiteId AccountId = new(Guid.NewGuid());
    private static readonly VisitorId PersonId = new(Guid.NewGuid());
    private static readonly DateTimeOffset OccurredAt = new(2026, 9, 29, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_PassesTheCommandsFieldsToTheStore_Unchanged()
    {
        var store = new FakePersonErasureStore();
        var handler = new Application.UseCases.ErasePerson.ErasePersonHandler(store);

        await handler.HandleAsync(new Application.UseCases.ErasePerson.ErasePerson(AccountId, PersonId, OccurredAt), CancellationToken.None);

        var call = Assert.Single(store.Requested);
        Assert.Equal(AccountId, call.AccountId);
        Assert.Equal(PersonId, call.PersonId);
        Assert.Equal(OccurredAt, call.OccurredAt);
    }

    [Theory]
    [InlineData(PersonErasureOutcome.Requested)]
    [InlineData(PersonErasureOutcome.AlreadyRequested)]
    [InlineData(PersonErasureOutcome.UnknownPerson)]
    public async Task HandleAsync_PassesTheStoresOwnOutcomeThrough(PersonErasureOutcome outcome)
    {
        var store = new FakePersonErasureStore(outcome);
        var handler = new Application.UseCases.ErasePerson.ErasePersonHandler(store);

        var result = await handler.HandleAsync(
            new Application.UseCases.ErasePerson.ErasePerson(AccountId, PersonId, OccurredAt), CancellationToken.None);

        Assert.Equal(outcome, result);
    }
}
