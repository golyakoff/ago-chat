namespace Ago.Chat.Worker;

/// <summary>Bound from <c>SiteErasureJob:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class SiteErasureJobOptions
{
    public const string SectionName = "SiteErasureJob";

    /// <summary>How often a sweep cycle runs. Deliberately the same default as
    /// <see cref="ConversationErasureJobOptions.Interval"/> - a site's own removal is gated on its
    /// conversations having drained (<see cref="SiteErasureQuery.HasAnyConversationAsync"/>), so
    /// running this job faster than the conversation job would only mean more no-op ticks spent
    /// finding conversations still remain; an operational default, not a measurement.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many sites one sweep cycle claims. Small - a site's own tick reaches Keycloak once
    /// per operator plus a cache-invalidation publish, the same "external I/O per item, not a row"
    /// reasoning <see cref="ConversationErasureJobOptions.BatchSize"/> gives.</summary>
    public int BatchSize { get; set; } = 10;
}
