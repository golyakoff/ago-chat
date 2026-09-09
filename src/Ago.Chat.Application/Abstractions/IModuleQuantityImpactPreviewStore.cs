using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-88`/`adr/0093`: the write-side port for <see cref="ModuleQuantityImpactPreview"/> - "chat asks
/// how many, the module answers, chat only remembers the answer to compare a later confirm against"
/// (`ModuleQuantityImpactPreview`'s own remarks). The identical "a state change and its integration
/// event, committed together" shape <see cref="IModuleQuantityGrantStore"/> already establishes for
/// the sibling grant, applied here to a question instead of a fact.
/// </summary>
public interface IModuleQuantityImpactPreviewStore
{
    /// <summary>The current preview row for one site's module, or <see langword="null"/> when none
    /// has ever been requested - the identical "missing means nothing, not an error" shape
    /// <see cref="IModuleQuantityGrantStore.GetQuantityAsync"/> uses for an analogous absence.</summary>
    Task<ModuleQuantityImpactPreview?> TryGetAsync(SiteId siteId, ModuleKey moduleKey, CancellationToken cancellationToken);

    /// <summary>Overwrites whatever preview row already existed with a fresh, unanswered one for
    /// <paramref name="requestedQuantity"/>, and enqueues <c>ModuleQuantityImpactRequested</c> in the
    /// same transaction as the row it describes (rule 4) - the same reason
    /// <see cref="IModuleQuantityGrantStore.GrantAsync"/> is not a read followed by a separate
    /// save.</summary>
    Task RequestAsync(
        SiteId siteId, ModuleKey moduleKey, int requestedQuantity, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Applies the module's own answer - but only when <paramref name="answeredQuantity"/> still
    /// matches the pending row's own <see cref="ModuleQuantityImpactPreview.RequestedQuantity"/>. A
    /// mismatch means either a late answer to a question the owner has since replaced with a new one
    /// (<see cref="RequestAsync"/> already overwrote it), or an answer for a site/module with no
    /// pending row at all (nothing here ever asked); either way this call is a silent no-op, never an
    /// error - the identical "acknowledge, do nothing, and do not record a row for a message this
    /// consumer never actually acted on" shape
    /// <c>Ago.Calendar.Worker.ModuleQuantityGrantedConsumer</c>'s own remarks describe for a grant
    /// meant for a different module.
    /// </summary>
    Task AnswerAsync(
        SiteId siteId, ModuleKey moduleKey, int answeredQuantity, int affectedCount,
        IReadOnlyList<string> affectedItemDisplayNames, DateTimeOffset now, CancellationToken cancellationToken);
}
