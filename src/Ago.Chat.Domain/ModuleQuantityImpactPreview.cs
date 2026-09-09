namespace Ago.Chat.Domain;

/// <summary>
/// `23-88`: "how many of this site's module K's own countable things would this candidate quantity
/// exceed, and what are they called" - the async question a platform owner asks before confirming a
/// downgrade, answered by the module (the calendar add-on's own worker count is the first real
/// caller) over the identical outbox mechanism <see cref="ModuleQuantityGrant"/> already uses for the
/// grant itself, never a live synchronous call (this item's own Decided section: a call made only to
/// answer "how many" must not make lowering a quota depend on the module being reachable, and the
/// same primitive must also work from an unattended future trigger with no human to show anything
/// to).
///
/// <para><b>A snapshot, one row per (site, module) - the identical "the natural key is the identity"
/// shape <see cref="ModuleQuantityGrant"/>'s own remarks state for itself, for the identical reason:
/// an owner previews one candidate number at a time for one module, never several in flight
/// together.</b> Requesting a new preview overwrites whatever answer (or non-answer) was sitting
/// here - the owner asking again after changing their mind supersedes, never queues behind, the
/// question they no longer care about.</para>
///
/// <para><b>Opaque to Chat, exactly like <see cref="ModuleQuantityGrant"/> itself.</b> This type
/// carries a requested number, an answered count, and a list of display strings the module chose to
/// hand back - it does not know or care whether those strings name a worker, a master, or anything
/// else. <see cref="AffectedItemDisplayNames"/> is the identical "an opaque payload the module fills
/// in and chat only carries" shape <c>ModuleStep.Payload</c> already establishes for a booking-flow
/// step's own content.</para>
///
/// <para><b>Not the write-time safety check itself - this type only remembers what was last shown.</b>
/// The actual "does the write still match what the owner confirmed" comparison lives in
/// <c>GrantModuleQuantityAsOwnerHandler</c>/<c>GrantModuleQuantityHandler</c>, which read this row and
/// refuse when it disagrees - see those handlers' own remarks for why the true, unconditional safety
/// net is still the module's own live recompute at grant-application time
/// (`adr/0125`'s own lock-and-count), and this row is a UX/consent guard on top of it, not a
/// replacement for it.</para>
/// </summary>
public sealed class ModuleQuantityImpactPreview
{
    public SiteId SiteId { get; }

    public ModuleKey ModuleKey { get; }

    /// <summary>The candidate quantity the owner is considering - what the eventual write must match
    /// for <see cref="AffectedCount"/> to still be trusted as an answer to the right question.</summary>
    public int RequestedQuantity { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    /// <summary><see langword="null"/> until the module's own asynchronous answer arrives - the
    /// "still waiting" state a console polls through, the same three-state shape (requested,
    /// answered, or superseded by a newer request) every field on this row shares.</summary>
    public int? AffectedCount { get; private set; }

    /// <summary>Opaque display strings the module chose to hand back - see this type's own remarks.
    /// Empty (never <see langword="null"/>) until answered, and always empty when
    /// <see cref="AffectedCount"/> is zero.</summary>
    public IReadOnlyList<string> AffectedItemDisplayNames { get; private set; } = [];

    public DateTimeOffset? AnsweredAt { get; private set; }

    private ModuleQuantityImpactPreview(SiteId siteId, ModuleKey moduleKey, int requestedQuantity, DateTimeOffset requestedAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requestedQuantity);
        SiteId = siteId;
        ModuleKey = moduleKey;
        RequestedQuantity = requestedQuantity;
        RequestedAt = requestedAt;
    }

    // EF Core materialization only - never called by domain code.
    private ModuleQuantityImpactPreview()
    {
    }

    public static ModuleQuantityImpactPreview Request(SiteId siteId, ModuleKey moduleKey, int requestedQuantity, DateTimeOffset now) =>
        new(siteId, moduleKey, requestedQuantity, now);

    /// <summary>A fresh question supersedes whatever this row held before - never merged with a prior
    /// answer, since the prior answer was about a different candidate number and saying anything about
    /// this one from it would be a guess dressed as data.</summary>
    public void Reset(int requestedQuantity, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requestedQuantity);
        RequestedQuantity = requestedQuantity;
        RequestedAt = now;
        AffectedCount = null;
        AffectedItemDisplayNames = [];
        AnsweredAt = null;
    }

    /// <summary>The module's own answer arrived. <paramref name="answeredQuantity"/> is checked
    /// against <see cref="RequestedQuantity"/> by the caller before this is invoked (the store's own
    /// remarks: a redelivered or late answer to a question this row has already moved past is
    /// discarded, never applied) - this method itself only records the fact once that check has
    /// already passed.</summary>
    public void Answer(int affectedCount, IReadOnlyList<string> affectedItemDisplayNames, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(affectedCount);
        AffectedCount = affectedCount;
        AffectedItemDisplayNames = affectedItemDisplayNames;
        AnsweredAt = now;
    }
}
