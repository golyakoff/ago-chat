using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.AiAddOn;
using Ago.Chat.Domain;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-04`: gate composition for the tests in this project whose subject is not the gate.
///
/// <para>Two shapes, deliberately. <see cref="Allowing"/> builds a real <see cref="AiProcessingGate"/>
/// over in-memory stand-ins, for tests that touch no database at all (the rate-limiter tests, whose
/// whole point is an HTTP header). <see cref="Real"/> builds one over the *real* Postgres-backed
/// <c>AiAddOnReadStore</c> and <c>ModuleQuantityGrantStore</c> - the shape
/// <see cref="ConversationCategorizationJobTests"/> uses, because "a conversation closed before the
/// cut-off is never categorised" is only proven when the cut-off is read back out of a real row by the
/// real query.</para>
///
/// <para>The module key is a literal here because `Ago.Chat.Architecture.Tests`' module-key literal
/// guard scans <c>src/</c> only - the deployment's own key has to be spellable somewhere a test can
/// set it, and this is that somewhere.</para>
/// </summary>
internal static class AiGateFixtures
{
    public const string ModuleKey = "ai";

    public static AiAddOnOptions Options => new() { ModuleKey = ModuleKey };

    /// <summary>A gate that lets everything through, with no database behind it.</summary>
    public static AiProcessingGate Allowing() =>
        new(new AlwaysEnabledReadStore(), new AlwaysGrantedGrantStore(), Options);

    private sealed class AlwaysEnabledReadStore : IAiAddOnReadStore
    {
        public Task<AiAddOnEnablementState?> GetForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
            Task.FromResult<AiAddOnEnablementState?>(new AiAddOnEnablementState(true, DateTimeOffset.MinValue));
    }

    private sealed class AlwaysGrantedGrantStore : IModuleQuantityGrantStore
    {
        public Task<int> GetQuantityAsync(SiteId siteId, ModuleKey moduleKey, CancellationToken cancellationToken) =>
            Task.FromResult(1);

        public Task<IReadOnlyDictionary<ModuleKey, int>> GetAllForSiteAsync(
            SiteId siteId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<ModuleKey, int>>(new Dictionary<ModuleKey, int>());

        public Task<IReadOnlyList<ModuleQuantityGrant>> GetGrantsForSiteAsync(
            SiteId siteId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ModuleQuantityGrant>>([]);

        public Task GrantAsync(
            SiteId siteId, ModuleKey moduleKey, int quantity, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SetUnconditionalGrantAsync(
            SiteId siteId, ModuleKey moduleKey, bool unconditionallyGranted, string setBy, string reason,
            DateTimeOffset now, CancellationToken cancellationToken, DateTimeOffset? expiresAt = null) =>
            Task.CompletedTask;
    }
}
