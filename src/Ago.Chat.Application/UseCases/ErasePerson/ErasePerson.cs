using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ErasePerson;

/// <summary>
/// `adr/0189`/`26-275` slice #3: the fact arriving at chat that the calendar has erased its own half of
/// this person (Option A, issue 1815) and cleared it to proceed with the rest - carried by the
/// <c>PersonErased</c> event, consumed by <c>PersonErasedConsumer</c> (<c>Ago.Chat.Worker</c>).
/// </summary>
/// <param name="AccountId">The module's word for what this product calls a site (`adr/0093`) - the same
/// value <c>RegisterExternalPerson.SiteId</c> is for the opposite-direction event.</param>
/// <param name="PersonId">Chat's own <see cref="VisitorId"/> for this person - the same id space
/// `adr/0184` already uses for the person registry, since the calendar's <c>PersonId</c> and chat's
/// <see cref="VisitorId"/> agree by construction.</param>
/// <param name="OccurredAt">When the calendar's own erasure committed - recorded as this person's
/// <c>erasure_requested_at</c>, the same "when the fact was established, not when this consumer ran"
/// reasoning <c>RegisterExternalPerson.RegisteredAt</c>'s own remarks give for the opposite direction.</param>
public sealed record ErasePerson(SiteId AccountId, VisitorId PersonId, DateTimeOffset OccurredAt);
