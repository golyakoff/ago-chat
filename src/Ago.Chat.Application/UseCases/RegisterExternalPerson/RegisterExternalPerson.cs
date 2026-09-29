using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RegisterExternalPerson;

/// <summary>
/// `adr/0184` decision 2: a module took a booking with no chat origin and minted a person id locally;
/// this is that fact arriving at the registry (through the <c>PersonRegistered</c> consumer). Chat
/// creates the Person under exactly <paramref name="PersonId"/>, so the module's reference and the
/// registry agree by construction.
/// </summary>
/// <param name="Phone">E.164 as the module sent it - recorded as a <c>Phone</c> contact detail, source
/// <c>Visitor</c> (the person typed it into a booking form; no operator stood between).</param>
/// <param name="Name">What the person typed as their name, or <see langword="null"/>.</param>
/// <param name="RegisteredAt">When the module registered the person - the contact details' own
/// <c>recorded_at</c>, since that is when the fact was established, not when this consumer ran.</param>
/// <param name="Email">`26-268`§3/`adr/0188`: the client's email, when the module's own write collected
/// one and shape-validated it before staging its own outbox event - today only ever non-null from a
/// calendar manual-booking mint (`EnterManualBookingHandler`), since the public widget booking form
/// collects no email. Recorded as an <see cref="VisitorContactDetailKind.Email"/> contact detail,
/// source <see cref="VisitorContactDetailSource.Visitor"/> - the identical treatment <paramref
/// name="Phone"/>/<paramref name="Name"/> already get, for the identical reason: no operator stood
/// between the client and the fact, whichever human typed it. <see langword="null"/> when the module
/// sent none, which is why this parameter defaults to it - an un-updated publisher's payload simply
/// omits the field (CLAUDE.md's own additive-and-backward-safe rule for a wire contract).</param>
public sealed record RegisterExternalPerson(
    SiteId SiteId, VisitorId PersonId, string Phone, string? Name, DateTimeOffset RegisteredAt,
    string? Email = null);
