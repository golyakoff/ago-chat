using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `25-04`: the write-side port for <see cref="AiAddOnEnablement"/>. Plain load/save rather than a
/// one-call use case like <see cref="IModuleQuantityGrantStore.GrantAsync"/>, because nothing here
/// crosses a product boundary: enabling the AI add-on changes only this deployment's own behaviour and
/// publishes no integration event, so rule 4's "state change and its integration event in one
/// transaction" has no event to carry.
/// </summary>
public interface IAiAddOnEnablementRepository
{
    /// <summary><see langword="null"/> for a site that has never enabled or disabled the add-on - the
    /// "off by default, expressed as the absence of a row" shape <see cref="AiAddOnEnablement"/>'s own
    /// remarks describe.</summary>
    Task<AiAddOnEnablement?> GetForSiteAsync(SiteId siteId, CancellationToken cancellationToken);

    /// <summary>Inserts a new row or updates the tracked one - the identical upsert shape
    /// <see cref="IModuleTaskChannelPreferenceRepository"/> already uses for a natural-key aggregate.</summary>
    Task SaveAsync(AiAddOnEnablement enablement, CancellationToken cancellationToken);
}
