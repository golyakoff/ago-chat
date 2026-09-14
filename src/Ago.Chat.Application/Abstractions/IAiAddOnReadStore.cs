using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `25-04`: the read-side port the gate uses - hand-written SQL over the write model, never through the
/// aggregate (`adr/0004`), the same split <see cref="IConversationReadStore"/> already makes against
/// <see cref="IConversationRepository"/>.
///
/// <para><b>Why a read store at all, when <see cref="IAiAddOnEnablementRepository"/> could answer the
/// same question.</b> <c>Ago.Chat.Worker.ConversationCategorizationJob</c> asks this once per candidate
/// conversation, inside a fresh scope, on a sweep that exists to be cheap - loading a tracked EF
/// aggregate to read two scalars would put a change tracker in a path that will never write. The write
/// path (<c>EnableAiAddOnHandler</c>) still goes through the repository, because it needs the aggregate's
/// own invariants.</para>
///
/// <para><b>Deliberately <em>not</em> cached.</b> `caching.md`/rule 8: the answer gates whether personal
/// data leaves the deployment, so a tenant who turns the add-on off must stop transmission on the next
/// call, not at the end of a TTL. The item's own point 6 promises exactly that ("с момента отключения
/// передача прекращается").</para>
/// </summary>
public interface IAiAddOnReadStore
{
    /// <summary><see langword="null"/> when this site has no enablement row at all - off, which is every
    /// tenant until they act.</summary>
    Task<AiAddOnEnablementState?> GetForSiteAsync(SiteId siteId, CancellationToken cancellationToken);
}

/// <summary>`25-04`: the two facts the gate needs, and nothing else. <paramref name="EffectiveFrom"/> is
/// <see langword="null"/> exactly when <paramref name="IsEnabled"/> is <see langword="false"/>
/// (<see cref="AiAddOnEnablement.EffectiveFrom"/>'s own definition), so a caller that forgets the flag
/// still cannot let anything through.</summary>
public sealed record AiAddOnEnablementState(bool IsEnabled, DateTimeOffset? EffectiveFrom);
