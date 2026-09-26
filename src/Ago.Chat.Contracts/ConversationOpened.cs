namespace Ago.Chat.Contracts;

/// <summary>
/// `adr/0186` S1: the analytics pipeline's own attribution fact for a conversation's start -
/// `docs/design/analytics-precompute.md` §4.1/§9. Named <c>ConversationOpened</c>, not
/// <c>ConversationStarted</c>, deliberately different from the domain event
/// <c>Ago.Chat.Domain.ConversationStarted</c> it is mapped from - the same domain-event/contract
/// naming split <c>ConversationClosed</c>/<c>ConversationEnded</c> already established, so
/// <c>ConversationOpenedMapper</c> can bring both types into scope with no alias
/// (`feedback_no_type_alias_for_namespace_collisions`: rename the colliding type, never
/// <c>using Alias = ...</c>).
///
/// <para><b>Flagged for the ago-analytics ingest consumer (S4) to match.</b> The design doc's own raw
/// event-type vocabulary (§6) names this fact <c>ConversationStarted</c> - the ClickHouse
/// <c>analytics_events.event_type</c> value the design expects. This codebase's own convention is that
/// the wire <see cref="EventEnvelope.Type"/> is always the C# contract's own name
/// (<c>nameof(ConversationOpened)</c>), and the domain/contract naming split above means this contract
/// cannot itself be called <c>ConversationStarted</c>. So the envelope actually on the broker carries
/// <c>Type == "ConversationOpened"</c>, not the literal string the design doc's vocabulary table names.
/// S4's consumer must either subscribe to <c>ConversationOpened</c> and translate it to the ClickHouse
/// row's <c>event_type = 'ConversationStarted'</c> at ingest time, or simply use
/// <c>"ConversationOpened"</c> as the vocabulary term throughout - either is fine, but the mismatch is
/// real and silent otherwise.</para>
///
/// <para><see cref="Channel"/> is the resolved label <c>OperatorAnalyticsReadStore</c>'s own read-time
/// query already computes at read time (`ChannelIdentity.Kind.ToString()`, or the literal
/// <c>"Widget"</c> for a visitor with no linked channel identity) - denormalized onto the event at
/// publish time exactly as `docs/design/analytics-precompute.md` §8.1 requires, so the future rollup
/// needs no join back to `ago_chat`'s own <c>channel_identities</c> table. Resolved through
/// <c>IChannelIdentityRepository.FindMostRecentForVisitorAsync</c> - the <b>most recently seen</b>
/// active identity - which is a different tie-break from the read store's own <b>earliest-seen</b> one;
/// that divergence already exists between those two call sites today (`OperatorAnalyticsReadStore`'s
/// own remarks: "a real, separate follow-up... not something bundled into" this kind of item) and this
/// change does not reconcile it, only reuses whichever port already existed for "the channel a caller
/// should attribute right now."</para>
///
/// <para><see cref="ReferrerHost"/>/<see cref="UtmCampaign"/> are the conversation's own captured
/// <c>TrafficSource</c> (`18-12`) - unverified, client-supplied, and possibly absent (a direct visit).
/// <see cref="TenantZone"/> is the owning <see cref="Ago.Chat.Domain.Site.TimeZone"/> as of publish time
/// (`adr/0186` §9) - a later zone change re-labels only future events, never this one.</para>
/// </summary>
public sealed record ConversationOpened(
    Guid ConversationId,
    Guid SiteId,
    Guid VisitorId,
    DateTimeOffset OccurredAt,
    Guid CorrelationId,
    string Channel,
    string? ReferrerHost,
    string? UtmCampaign,
    string TenantZone);
