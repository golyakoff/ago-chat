using Ago.Chat.Domain;

namespace Ago.Chat.Worker;

/// <summary>
/// `18-06`: per-channel-kind inactivity windows, not one global constant - the backlog item's own
/// scope note is explicit that a single number would "hide two meanings" (a widget conversation ends
/// when a browser tab closes; a channel conversation's identity survives, so the same visitor can
/// plausibly pick the thread back up hours later). <see cref="ChannelInactivityWindows"/> is what lets
/// each <see cref="ChannelKind"/> differ from every other, not just from the widget default.
///
/// <para>Every window below is a <b>stated default, not a measurement</b> (CLAUDE.md bans invented
/// "typical" numbers) - chosen from the reasoning the backlog item itself gives (a widget session has
/// no return-visitor value once the tab is gone; a channel identity is durable and worth waiting
/// longer on), not from any claimed industry figure.</para>
/// </summary>
public sealed class AutoCloseInactiveConversationsJobOptions
{
    public const string SectionName = "AutoCloseInactiveConversationsJob";

    /// <summary>`26-138`: raised from 5 to 15 minutes. The release pass is not time-critical - a quiet
    /// conversation's operator slot freeing three cycles later than before is immaterial next to what the
    /// old cadence cost: at 5-minute ticks every idle conversation was re-evaluated (and, before `26-119`
    /// /`26-138` tightened the claim predicate, re-assigned and re-pushed) twelve times an hour. Fifteen
    /// minutes is the coarsest tick that still reacts to a genuinely new visitor message within a support
    /// SLA's own tolerance, and the claim-side marker (`Conversation.ReleasedWaitingAtSequence`) is what
    /// actually stops the churn - this interval only decides how often the pass looks.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Candidates closed per (channel-kind, tick) pair - the same batching shape
    /// <c>AttachmentOrphanSweepJobOptions.BatchSize</c> already uses, so one tick with an unusually
    /// large backlog cannot hold a transaction-free scan open indefinitely.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>`25-118`: renamed in meaning, not in name, from "closes after this long" to "releases
    /// back to `Waiting` after this long" - a conversation whose visitor has no `channel_identities` row
    /// (`ChannelKind`'s own remarks: a widget visitor is identified by a signed token, never a channel
    /// address) and no message either direction for this long is <see cref="Domain.Conversation.ReleaseToQueue"/>-d,
    /// freeing the assigned operator's capacity slot immediately - it is no longer actually closed at
    /// this window (see <see cref="WidgetCloseWindow"/> for that). One hour: long enough that a visitor
    /// re-reading a reply and typing a follow-up is never caught by it, short enough that a released
    /// operator capacity slot is not needlessly held by a visitor who has simply closed the tab.</summary>
    public TimeSpan WidgetInactivityWindow { get; set; } = TimeSpan.FromHours(1);

    /// <summary>`25-118`: the second, longer widget-only window this item adds -
    /// `docs/backlog/25-118-*`'s own "Answered" design: a widget conversation (`Assigned` *or*
    /// `Waiting` - unlike <see cref="WidgetInactivityWindow"/>, which only ever touches `Assigned`) with
    /// no message either direction for this much longer stretch is actually
    /// <see cref="Domain.Conversation.Close"/>-d, through the same path <see cref="WidgetInactivityWindow"/>
    /// used to reach directly. Defaulted to seven days to match `Ago.Chat.Api.Auth.JwtTokenService
    /// .VisitorTokenLifetime` - a widget visitor's own signed identity is designed to persist (and
    /// auto-renew on return) for exactly this long, and there is no point keeping a conversation
    /// resumable past the point its visitor's own token has already lapsed. `Ago.Chat.Worker` cannot
    /// reference `Ago.Chat.Api` (the two are separate hosts, `docs/adr/0013-*`), so this default is a
    /// plain <see cref="TimeSpan"/> literal, not a shared constant - if `VisitorTokenLifetime` ever
    /// changes, this default has to be changed here too, by hand; nothing enforces the two staying
    /// equal.</summary>
    public TimeSpan WidgetCloseWindow { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Applied to any `ChannelKind` not given its own entry in
    /// <see cref="ChannelInactivityWindows"/>. Twenty-four hours: a durable identity (a phone number, a
    /// MAX/Telegram account) can plausibly reply the next business day and still be continuing the same
    /// thread, which is the "real continuity value" the backlog item names as the reason channel
    /// conversations get materially longer than widget ones.</summary>
    public TimeSpan DefaultChannelInactivityWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Per-kind overrides, keyed by `ChannelKind`'s own member name (the config binder's
    /// default string-to-enum conversion) - e.g. <c>{"Sms": "12:00:00"}</c> to give SMS its own window
    /// shorter than <see cref="DefaultChannelInactivityWindow"/>. Empty by default: every kind falls
    /// back to the shared default until an operator's real usage says otherwise (a number this item is
    /// not in a position to invent for four channels it cannot yet measure).</summary>
    public Dictionary<ChannelKind, TimeSpan> ChannelInactivityWindows { get; set; } = [];

    /// <summary><see langword="null"/> means "no channel_identities row" (widget); otherwise the
    /// per-kind override if one is configured, else <see cref="DefaultChannelInactivityWindow"/>.
    /// </summary>
    public TimeSpan WindowFor(ChannelKind? channelKind) =>
        channelKind is { } kind
            ? ChannelInactivityWindows.GetValueOrDefault(kind, DefaultChannelInactivityWindow)
            : WidgetInactivityWindow;
}
