using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.UseCases.ErasePerson;

/// <summary>
/// `adr/0189`/`26-275` slice #3: the registry's half of "the calendar erased its own record and told
/// chat to erase the rest." A thin pass-through onto <see cref="IPersonErasureStore"/> today - there is
/// no domain logic to apply, unlike <c>RegisterExternalPersonHandler</c>'s own use case, which builds a
/// <see cref="Ago.Chat.Domain.Visitor"/> and its contact details before handing them to its own store.
///
/// <para><b>Kept as its own Application-layer handler rather than having <c>PersonErasedConsumer</c> call
/// <see cref="IPersonErasureStore"/> directly.</b> The dependency rule alone would not forbid the
/// shortcut - <c>Ago.Chat.Worker</c> is a host and may reference <c>Ago.Chat.Infrastructure.Postgres</c>
/// directly - but every other consumer in this Worker resolves a scoped Application handler
/// (<c>PersonRegisteredConsumer</c> → <c>RegisterExternalPersonHandler</c>), never an Infrastructure port
/// by itself. Matching that seam keeps the consumer swappable without touching Infrastructure wiring, and
/// keeps this write testable (the fake in this project's own tests) without a real Postgres - the same
/// two reasons <c>RegisterExternalPersonHandler</c> is not simply inlined into its own consumer.</para>
/// </summary>
public sealed class ErasePersonHandler(IPersonErasureStore erasures)
{
    public Task<PersonErasureOutcome> HandleAsync(ErasePerson command, CancellationToken cancellationToken) =>
        erasures.RequestErasureIfPresentAsync(command.AccountId, command.PersonId, command.OccurredAt, cancellationToken);
}
