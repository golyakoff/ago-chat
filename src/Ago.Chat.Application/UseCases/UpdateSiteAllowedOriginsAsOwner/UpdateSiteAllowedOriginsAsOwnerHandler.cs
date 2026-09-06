using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Application.UseCases.RegisterSite;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.UpdateSiteAllowedOriginsAsOwner;

/// <summary>
/// `23-48`: the platform owner's own write for a tenant's allowed origins - see
/// <see cref="UpdateSiteAllowedOriginsAsOwner"/>'s own remarks for why this has no self-service
/// sibling to be a deliberate departure from.
///
/// <para><b>This handler performs no authorization check, and that is deliberate rather than an
/// omission</b> - the identical reasoning every other owner-write handler in this codebase gives for
/// itself (<c>EnableModuleForSiteAsOwnerHandler</c>'s own remarks are the fullest statement of it).
/// The fact that authorizes this call is a `platform-owner` realm role Keycloak signs into the token
/// (`adr/0032`), decided once by `RequirePlatformOwner` on the one route that resolves this handler
/// (<c>OwnerSiteAllowedOriginsEndpoints</c>). `Ago.Chat.Application` has no port that can see a claim,
/// so a second check here would be a second, weaker copy of the same rule, free to drift from the
/// first the moment either changes.</para>
///
/// <para><b>Validates each origin with the same <see cref="OriginValidator"/> `10-02`'s registration
/// handler already uses</b> - not a second wording for the identical rule
/// (`docs/backlog/23-48-*.md`'s own instruction: refuse rather than store something that can never
/// match a browser's literal `Origin` header comparison). An empty list is refused before any origin
/// is even checked: nothing chosen would leave the widget reachable from anywhere, the same "decide,
/// don't default to a state nobody chose" shape `EnableModuleForSiteAsOwnerHandler.ExpiresAt`'s own
/// remarks describe for its own required field.</para>
///
/// <para><b>Real "not found", not an info-hiding one</b> - the identical reasoning
/// <c>GetSiteForOwnerHandler</c>'s own remarks give: the platform owner may legitimately name any site
/// on the deployment, so a real <see cref="ConversationErrors.SiteNotFound"/> is the honest answer,
/// not a leak (`23-43` is about the console's own navigation and error text, not about this handler
/// telling its one legitimate caller the truth).</para>
///
/// <para><b>Two outbox rows from one write</b> - the same "one domain event, two integration events"
/// shape <c>UpdateWidgetConfigHandler</c> and <c>UpdateContactVisibilityHandler</c> already use, here
/// because <see cref="SiteAllowedOriginsUpdated"/> itself is mapped twice
/// (<see cref="SiteAllowedOriginsUpdatedMapper"/>/<see cref="SiteAllowedOriginsChangedMapper"/>'s own
/// remarks): the site-config cache (read by the widget handshake and the hub's layer-2 check) and the
/// CORS-layer cache (read by the preflight's layer-1 check) are two different cache shapes with two
/// different consumers, and both must be evicted in the same transaction as the write or a save that
/// appears to work could take effect an hour later (`23-48`'s own brief).</para>
/// </summary>
public sealed class UpdateSiteAllowedOriginsAsOwnerHandler(
    ISiteRepository sites, IOutboxWriter outbox, IIdGenerator idGenerator, IClock clock)
{
    public async Task<Result<IReadOnlyList<string>>> HandleAsync(
        UpdateSiteAllowedOriginsAsOwner command, CancellationToken cancellationToken)
    {
        if (command.AllowedOrigins.Count == 0)
        {
            return ConversationErrors.SiteInvalidOrigin(
                "A site must keep at least one allowed origin - clearing the list would lock the widget out of every page.");
        }

        // Deduplicated, order-preserved: a caller who pasted the same origin twice (or is re-saving a
        // list the console itself rendered) should not persist two identical entries, but the order
        // they typed them in is otherwise theirs to keep.
        var origins = command.AllowedOrigins.Distinct(StringComparer.Ordinal).ToList();

        foreach (var origin in origins)
        {
            var originError = OriginValidator.Validate(origin);
            if (originError is not null)
            {
                return ConversationErrors.SiteInvalidOrigin(originError);
            }
        }

        var site = await sites.GetByIdAsync(command.SiteId, cancellationToken);
        if (site is null)
        {
            return ConversationErrors.SiteNotFound(command.SiteId.Value);
        }

        var now = clock.UtcNow;
        site.UpdateAllowedOrigins(origins, now);

        var originsChanged = site.DomainEvents.OfType<SiteAllowedOriginsUpdated>().Single();
        outbox.Enqueue(SiteAllowedOriginsUpdatedMapper.ToEnvelope(originsChanged, idGenerator));
        outbox.Enqueue(SiteAllowedOriginsChangedMapper.ToEnvelope(originsChanged, idGenerator));
        site.ClearDomainEvents();

        await sites.SaveAsync(site, cancellationToken);

        return origins;
    }
}
