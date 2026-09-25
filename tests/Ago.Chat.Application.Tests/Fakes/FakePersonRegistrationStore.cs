using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`adr/0184`: records what the handler asked to register, and answers with whatever outcome
/// the test scripted - the store's own idempotency is the real adapter's to prove, against Postgres.</summary>
public sealed class FakePersonRegistrationStore(PersonRegistrationOutcome outcome = PersonRegistrationOutcome.Created)
    : IPersonRegistrationStore
{
    public List<(Visitor Person, IReadOnlyList<VisitorContactDetail> Details)> Registered { get; } = [];

    public Task<PersonRegistrationOutcome> RegisterIfAbsentAsync(
        Visitor person, IReadOnlyList<VisitorContactDetail> contactDetails, CancellationToken cancellationToken)
    {
        Registered.Add((person, contactDetails));
        return Task.FromResult(outcome);
    }
}
