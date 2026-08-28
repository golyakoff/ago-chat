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

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Candidates closed per (channel-kind, tick) pair - the same batching shape
    /// <c>AttachmentOrphanSweepJobOptions.BatchSize</c> already uses, so one tick with an unusually
    /// large backlog cannot hold a transaction-free scan open indefinitely.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>A conversation whose visitor has no `channel_identities` row (`ChannelKind`'s own
    /// remarks: a widget visitor is identified by a signed token, never a channel address) is closed
    /// after this long with no message either direction. One hour: long enough that a visitor
    /// re-reading a reply and typing a follow-up is never caught by it, short enough that a released
    /// operator capacity slot is not needlessly held by a visitor who has simply closed the tab.</summary>
    public TimeSpan WidgetInactivityWindow { get; set; } = TimeSpan.FromHours(1);

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
