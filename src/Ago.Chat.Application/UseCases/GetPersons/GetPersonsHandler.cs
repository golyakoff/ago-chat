using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetPersons;

/// <summary>
/// `adr/0184` decision 4: chat is the account's person registry, and this is the registry's read for
/// display - the console asks for the people behind the person ids a calendar screen carries and merges
/// the answer onto that screen client-side. No server-to-server person read exists; the calendar never
/// calls this.
///
/// <para><b>Gated on <see cref="Permission.ConversationRead"/>, the same permission that already lets an
/// operator see this person's contact details on the conversation panel.</b> A person's name and channels
/// are the identical facts <c>ListVisitorContactDetailsHandler</c> already serves per conversation; reading
/// them by person id is not a wider capability, only a different key. Masking follows the same rule for
/// the same reason - the account's own rung, resolved once for the whole batch.</para>
///
/// <para><b>Tenant-isolated by construction.</b> A person id that resolves to another account's visitor
/// is simply absent from the answer, exactly as an unknown id is - the "wrong tenant reads like no such
/// row" shape every cross-tenant guard in this codebase already uses. The single-id endpoint turns that
/// absence into <see cref="PersonErrors.NotFound"/>; the batch endpoint returns what it found.</para>
///
/// <para><b>Bounded.</b> At most <see cref="MaxIds"/> ids per call - a page of bookings, not an export.
/// Two round trips regardless of the count (the visitors, then every contact detail for them), never one
/// per person.</para>
/// </summary>
public sealed class GetPersonsHandler(
    IVisitorRepository visitors,
    IVisitorContactDetailRepository contactDetails,
    IPermissionChecker permissions,
    GetSiteConfigByIdHandler siteConfig)
{
    public const int MaxIds = 200;

    public async Task<Result<IReadOnlyList<PersonProfileDto>>> HandleAsync(GetPersons query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.ConversationRead, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read conversations for this site.");
        }

        var ids = query.PersonIds.Distinct().ToList();
        if (ids.Count > MaxIds)
        {
            return PersonErrors.TooManyIds(MaxIds);
        }

        if (ids.Count == 0)
        {
            return Result<IReadOnlyList<PersonProfileDto>>.Success([]);
        }

        var found = await visitors.GetManyByIdsAsync(ids, cancellationToken);
        var own = found.Values.Where(v => v.SiteId == query.SiteId).OrderBy(v => v.FirstSeenAt).ToList();
        if (own.Count == 0)
        {
            return Result<IReadOnlyList<PersonProfileDto>>.Success([]);
        }

        // `GetSiteConfigById.GetSiteConfigById`, not a bare `GetSiteConfigById`: the query type shares
        // its name with its own namespace (ListVisitorContactDetailsHandler's own precedent).
        var config = await siteConfig.HandleAsync(new GetSiteConfigById.GetSiteConfigById(query.SiteId), cancellationToken);
        var masked = config?.ContactVisibility == ContactVisibility.MaskedWithReveal;

        var details = await contactDetails.GetForVisitorsAsync(own.Select(v => v.Id).ToList(), cancellationToken);
        var byVisitor = details.ToLookup(d => d.VisitorId);

        IReadOnlyList<PersonProfileDto> profiles = own
            .Select(visitor => ToProfile(visitor, byVisitor[visitor.Id], masked))
            .ToList();

        return Result<IReadOnlyList<PersonProfileDto>>.Success(profiles);
    }

    private static PersonProfileDto ToProfile(Visitor visitor, IEnumerable<VisitorContactDetail> details, bool masked)
    {
        var ordered = details.OrderBy(d => d.RecordedAt).ToList();

        // The most recently recorded name wins - the same "most recent wins" reduction
        // IVisitorContactDetailRepository.GetNamesForVisitorsAsync already applies for the queue.
        var displayName = ordered
            .Where(d => d.Kind == VisitorContactDetailKind.Name)
            .OrderByDescending(d => d.RecordedAt)
            .Select(d => d.Value)
            .FirstOrDefault();

        IReadOnlyList<PersonContactChannelDto> channels = ordered
            .Where(d => d.Kind != VisitorContactDetailKind.Name)
            .Select(d => new PersonContactChannelDto(
                d.Id.Value, d.Kind.ToString(), masked ? Mask(d.Value) : d.Value, masked, d.Verified,
                d.Assessment.ToString(), d.RecordedAt))
            .ToList();

        return new PersonProfileDto(visitor.Id.Value, displayName, channels, visitor.FirstSeenAt, visitor.LastSeenAt);
    }

    // The identical masking ListVisitorContactDetailsHandler applies - restated rather than shared so a
    // change to one panel's rule is a deliberate change to the other's too.
    private static string Mask(string value) =>
        value.Length <= 4
            ? new string('•', value.Length)
            : $"{value[..2]}{new string('•', value.Length - 4)}{value[^2..]}";
}
