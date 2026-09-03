using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `20-07`: the hot read path - "a site's enabled modules and their trigger arrays" - adr/0004's Dapper
/// side. Two real callers: <c>EnableModuleForSiteHandler</c>'s own trigger-overlap check (registration
/// time, low frequency) and the message pipeline's trigger match (every visitor message, on every site
/// with at least one module enabled - the reason this is a read store and not a plain EF query
/// through <see cref="IEnabledModuleRepository"/>, matching `caching.md`'s reasoning for every other
/// per-message read in this codebase).
/// </summary>
public interface IEnabledModuleReadStore
{
    Task<IReadOnlyList<EnabledModuleSummary>> GetForSiteAsync(SiteId siteId, CancellationToken cancellationToken);
}

/// <summary>Exactly what <see cref="TriggerCommandMatcher"/> and the registration-time overlap check
/// need, and nothing else about an <see cref="EnabledModule"/> row - no <see cref="EnabledModuleId"/>,
/// which neither caller has any use for.
///
/// <para><b>`22-02`: <see cref="Credential"/> rides along even though neither of those two callers
/// reads it.</b> The one caller that does - <c>RouteConversationToModuleHandler</c>, building an
/// <see cref="EnabledModuleEndpoint"/> per call - already reads this same row for
/// <see cref="EntryPoint"/>, so carrying the credential here avoids a second read store method for a
/// single extra field.</para></summary>
public sealed record EnabledModuleSummary(
    ModuleKey ModuleKey, IReadOnlyList<string> TriggerWords, Uri EntryPoint, ModuleCredential Credential);
