using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RegisterExternalPerson;

/// <summary>
/// `adr/0184` decision 2: the registry's half of "a booking with no chat origin mints a person id locally
/// and publishes <c>PersonRegistered</c>; chat consumes it and creates the Person."
///
/// <para><b>Idempotent on the person id (CLAUDE.md rule 5), and never an overwrite.</b> A redelivery, or
/// an announcement about a visitor chat had in fact already seen, finds the person present and changes
/// nothing - the registry's own facts outrank a booking form's. Everything the person is created with is
/// written in one transaction by <see cref="IPersonRegistrationStore"/>, so a crash can never leave a
/// person with no phone.</para>
///
/// <para><b>The person gets an emoji pair at registration, like every other visitor.</b> `25-56` decision
/// 5 assigns the pair at first contact and never again; for a person who first arrives through a booking
/// rather than a conversation, this <em>is</em> first contact - and <c>StartConversationHandler</c> only
/// assigns a pair to a visitor it creates itself, so a person registered here without one would reach the
/// operator's queue with no mnemonic the day they finally open a chat.</para>
///
/// <para>The contact details are recorded with source <see cref="VisitorContactDetailSource.Visitor"/>:
/// the person typed the phone and the name into a booking form themselves, and no operator stood between
/// them and this row. Never <c>Verified</c> - a booking form proves nothing about who controls the number
/// (<c>VisitorContactDetail</c>'s own remarks); the calendar's own verified-phone fact stays the calendar's.</para>
/// </summary>
public sealed class RegisterExternalPersonHandler(
    IPersonRegistrationStore registrations,
    IIdGenerator idGenerator,
    IVisitorEmojiPairGenerator emojiPairs)
{
    public async Task<PersonRegistrationOutcome> HandleAsync(RegisterExternalPerson command, CancellationToken cancellationToken)
    {
        var person = new Visitor(command.PersonId, command.SiteId, command.RegisteredAt);
        var (creature, food) = emojiPairs.NextPair();
        person.AssignEmojiPair(creature, food);

        var details = new List<VisitorContactDetail>(2);
        if (!string.IsNullOrWhiteSpace(command.Phone))
        {
            details.Add(VisitorContactDetail.RecordFromVisitor(
                new VisitorContactDetailId(idGenerator.NewId(command.RegisteredAt)), person.Id,
                VisitorContactDetailKind.Phone, command.Phone, command.RegisteredAt));
        }

        if (!string.IsNullOrWhiteSpace(command.Name))
        {
            details.Add(VisitorContactDetail.RecordFromVisitor(
                new VisitorContactDetailId(idGenerator.NewId(command.RegisteredAt)), person.Id,
                VisitorContactDetailKind.Name, command.Name, command.RegisteredAt));
        }

        return await registrations.RegisterIfAbsentAsync(person, details, cancellationToken);
    }
}
