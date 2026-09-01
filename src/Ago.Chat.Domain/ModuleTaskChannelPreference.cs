namespace Ago.Chat.Domain;

/// <summary>
/// `20-11`: one entry in a visitor's own priority-ordered list of additional contact channels for a
/// specific booking - the deferred second half of `20-09`'s own primary-phone gate. Scoped to a
/// <see cref="ModuleTaskId"/> (the chat-module concept `20-07` already built for "this conversation is
/// in the middle of running some module's flow"), not to a <see cref="ConversationId"/> or a visitor -
/// `20-11`'s own "Decided" section is explicit that this is a narrower, per-*booking* override sitting
/// in front of `14-13`'s own per-*visitor* <see cref="Visitor.PreferredChannelIdentityId"/>, and a
/// conversation can run more than one module task over its lifetime (each gets its own list).
///
/// <para><b>No separate "kind/address/verified-at" snapshot - a plain <see cref="ChannelIdentityId"/>
/// reference instead, the identical shape <see cref="Visitor.PreferredChannelIdentityId"/> already
/// uses.</b> `20-11`'s own Scope text imagines "a small ordered table (channel kind, address,
/// verified-at, priority)"; reading `14-15`'s real code shows every path that produces an additional
/// verified channel - another phone number via `ConfirmPhoneVerificationHandler`, a messenger identity
/// via `14-12`'s own linking - converges on exactly one place the evidence already lives:
/// <see cref="ChannelIdentity"/>. Copying its kind/address/verified-at fields into a second row would
/// duplicate a fact that can drift (an <see cref="ChannelIdentity.Unlink"/> would leave a stale copy
/// here with no way to know it happened), for no benefit - this table and <see cref="ChannelIdentity"/>
/// live in the same database, so there is no cross-process/cross-repository boundary to snapshot across
/// the way `20-09`'s own <c>Customer.phone_verified_at</c> snapshot exists for (a live join across a
/// product boundary Chat cannot make). A live foreign key, re-validated <see cref="ChannelIdentity.Active"/>
/// at read time exactly as `14-13`'s own <c>DeliverChannelMessageHandler.ResolvePreferredIdentityAsync</c>
/// already does for its own preference field, is the "reuse, don't invent a second data-flow pattern"
/// instruction taken literally rather than its literal wording followed past the point it still
/// applies.</para>
///
/// <para><b>"Independently verified before it earns a place" is enforced entirely by this being a
/// reference, not an assertion.</b> The only way a <see cref="ChannelIdentityId"/> value exists at all
/// is <see cref="ChannelIdentity.Link"/> - reached only through `14-12`'s inbound-message evidence or
/// `14-15`'s confirmed-code evidence, never through this type or its own use case. A visitor "claiming"
/// a channel has literally no code path that could produce a row here; the eligibility check
/// (<c>SetModuleTaskChannelPriorityListHandler</c>) can only ever accept an id that already survived one of
/// those two mechanisms.</para>
///
/// <para><see cref="Priority"/> is 1-based and unique within one <see cref="ModuleTaskId"/> - enforced
/// by construction (the handler always rewrites the entire list, assigning <c>index + 1</c>) and
/// backstopped by a real unique index, the same "index is the backstop, not the primary mechanism"
/// division `ChannelIdentityConfiguration`'s own remarks describe for its own uniqueness rule.</para>
/// </summary>
public sealed class ModuleTaskChannelPreference
{
    public ModuleTaskChannelPreferenceId Id { get; }

    public SiteId SiteId { get; }

    public ModuleTaskId ModuleTaskId { get; }

    /// <summary>Whose list this is - carried alongside <see cref="ModuleTaskId"/> rather than looked up
    /// through it, the same "aggregates stay independent, no loaded navigation" shape
    /// <see cref="ChannelIdentity.VisitorId"/> already uses for its own foreign reference.</summary>
    public VisitorId VisitorId { get; }

    public ChannelIdentityId ChannelIdentityId { get; }

    public int Priority { get; }

    public DateTimeOffset AddedAt { get; }

    public static ModuleTaskChannelPreference Add(
        ModuleTaskChannelPreferenceId id, SiteId siteId, ModuleTaskId moduleTaskId, VisitorId visitorId,
        ChannelIdentityId channelIdentityId, int priority, DateTimeOffset now)
    {
        if (priority < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(priority), priority, "Priority must be 1 or greater.");
        }

        return new ModuleTaskChannelPreference(id, siteId, moduleTaskId, visitorId, channelIdentityId, priority, now);
    }

    private ModuleTaskChannelPreference(
        ModuleTaskChannelPreferenceId id, SiteId siteId, ModuleTaskId moduleTaskId, VisitorId visitorId,
        ChannelIdentityId channelIdentityId, int priority, DateTimeOffset now)
    {
        Id = id;
        SiteId = siteId;
        ModuleTaskId = moduleTaskId;
        VisitorId = visitorId;
        ChannelIdentityId = channelIdentityId;
        Priority = priority;
        AddedAt = now;
    }

    // EF Core materialization only.
    private ModuleTaskChannelPreference()
    {
    }
}
