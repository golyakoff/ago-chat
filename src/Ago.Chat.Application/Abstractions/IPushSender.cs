namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `26-04`/`adr/0179` §5/`adr/0180`: the outbound half of operator push - the one port every future push
/// provider implements, carrying none of RuStore's (or any other provider's) own vocabulary.
/// `Ago.Chat.Infrastructure.RuStore.RuStorePushSender` is the only implementation today, registered
/// directly in <c>Ago.Chat.Worker</c>'s own composition root - no provider registry, no
/// <c>IPushSenderFactory</c> (`adr/0179` §5: a dispatch table with one entry is a guess about the
/// second). The port's signature not needing to move when the provider changed from FCM to RuStore
/// (`adr/0180`, written before a line of adapter code existed) is this codebase's own first evidence
/// that the layering call was right, not preparation for it.
///
/// <para><b>Why this lives in Application, not Infrastructure (CLAUDE.md rule 2).</b> The push provider
/// is an external resource reached over HTTP, so a handler that needs to send one (`26-05`'s
/// <c>NotifyOperatorDevicesHandler</c>) must depend on an abstraction it can fake in a unit test, never
/// on <see cref="System.Net.Http.HttpClient"/> directly. The alternative - calling a provider SDK from
/// the handler - would make the one rule that keeps <c>operator_devices</c> from rotting (a terminal
/// provider response revokes the row) untestable without a real RuStore project and a real service
/// token, neither of which exists in this deployment yet.</para>
///
/// <para><b>Resilience is not this interface's business</b>, the identical discipline
/// <see cref="IInboundChannelAdapter"/>'s own remarks state: an implementation is written as if RuStore
/// always answers; timeout, retry, circuit breaker and bulkhead are applied by wrapping it
/// (<c>Ago.Chat.Module.Push.ResilientPushSender</c> over <c>Ago.Platform.Resilience</c>), so this port
/// never sees Polly - <c>ChannelPortTests.ChannelPort_DoesNotKnowHowItIsProtected</c>'s own rule, applied
/// here for the identical reason.</para>
/// </summary>
public interface IPushSender
{
    /// <summary>
    /// Sends one push to one device.
    ///
    /// <para><b>The three-way outcome, and why none of it is thrown for a response RuStore actually
    /// gave.</b> `resilience.md`'s own rule for every other outbound provider in this codebase - "a
    /// response the provider actually answered but refused comes back as a value; anything shaped like
    /// 'the provider or the network failed' throws" (<see cref="IYooKassaPaymentsClient"/>'s own remarks
    /// state it in those words) - applies here without amendment. RuStore's send API answers every one
    /// of its five documented outcomes (`400`/`401`/`403`/`404`/`429`/`500` - `26-04`'s own backlog item
    /// has the full table) with a real, parseable JSON error body carrying <c>code</c>/<c>status</c>; a
    /// definitive answer, however unwelcome, is never retried, because retrying an answer already given
    /// would never change it. Only a response this class cannot parse into that shape at all - a broken
    /// connection, a timeout, an edge/CDN rejection with no application-level body - is thrown, because
    /// that is the one case where a second attempt might get a different, real answer.</para>
    ///
    /// <para><see cref="PushSendOutcome.TokenGone"/> is <see cref="PushSendOutcome.TransientFailure"/>'s
    /// sibling, not its synonym, and the two must never be confused: <c>TokenGone</c> means <em>this
    /// device's</em> registration is dead and the caller should revoke it
    /// (`IOperatorDeviceRepository.RevokeByTokenAsync`, `26-05`'s own job, out of this port's scope);
    /// <c>TransientFailure</c> covers everything else RuStore told us definitively that is not a
    /// statement about this device at all - including a bad or malformed service token, which is *our*
    /// credential's fault. Getting these two swapped would revoke every device in the table the moment
    /// the service token itself was ever wrong, rather than when an individual device's registration
    /// actually died - the exact danger `26-04`'s own backlog item names by name.</para>
    /// </summary>
    Task<PushSendOutcome> SendAsync(PushMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// One push, in provider-neutral terms - the shape `alertTextFor` (`ago-console`) already decided this
/// product's one notification is, not a guess at the intersection of two providers' APIs
/// (`adr/0179` §5's own remarks on why no more general "notification content" abstraction exists).
///
/// <para><see cref="DeviceToken"/> is the provider's own registration token,
/// <see cref="Domain.OperatorDevice.Token"/>'s current value at send time - a plain string here, the
/// same way it is a plain string on that aggregate, because neither this port nor that type needs to
/// parse or validate its shape, only carry it.</para>
///
/// <para><see cref="GroupKey"/> travels even though RuStore's own send API has no collapse-key field to
/// put it in (`adr/0180` §4a: <c>RemoteMessage.collapseKey</c> is documented as not currently taken into
/// account) - it is carried in <see cref="Data"/> instead, because the *client* still collapses on it
/// (`push-notifications.md`'s own "Idempotency, without an inbox row": the Android notification tag
/// <c>ago-conversation-{conversationId}</c>). The port keeps the concept because the receiving end still
/// needs it; only the transport-level field disappeared with the provider change.</para>
///
/// <para><see cref="TimeToLive"/> is new relative to `adr/0179`'s original FCM design, for the reason
/// <see cref="RecommendedTimeToLive"/>'s own remarks give in full: RuStore's own default when it is
/// absent is four weeks, which for "a visitor is waiting" is noise rather than a notification.</para>
/// </summary>
public sealed record PushMessage(
    string DeviceToken,
    string Title,
    string Body,
    string GroupKey,
    TimeSpan TimeToLive,
    IReadOnlyDictionary<string, string> Data)
{
    /// <summary>
    /// `26-04`'s own choice, stated with its reasoning rather than picked silently - CLAUDE.md rule 7
    /// forbids inventing a number without saying why, and `push-notifications.md`/`adr/0180` §6
    /// deliberately left this undecided for exactly this item to settle.
    ///
    /// <para><b>Five minutes, and why not the whole spectrum in between.</b> The two events this design
    /// ever pushes for - a conversation just assigned, or a visitor message on one already assigned -
    /// are both "something needs attention now" signals, not "something happened today" ones; RuStore's
    /// own four-week default is the wrong order of magnitude by roughly four thousand times, and picking
    /// anything measured in hours would repeat the same mistake at a smaller scale. Five minutes is
    /// chosen instead of something in the tens of seconds because RuStore's own delivery path is a
    /// distributor app polling on an interval this design does not know
    /// (`push-notifications.md`'s "Delivery is a distributor, not a socket") - a ttl too close to the
    /// realistic delivery latency would race a healthy delivery and lose some pushes to their own
    /// expiry before the distributor ever gets to them, which is worse than the notification arriving a
    /// little late. And five minutes is chosen over something in the tens of minutes because a
    /// notification that says "a visitor is waiting" stops being true well before RuStore's own
    /// documented `onDeletedMessages()` recovery hook would ever fire for it: by the time a shop's
    /// operator would plausibly still be checking their phone for it, the visitor has very likely been
    /// helped some other way (the desktop console's own live alert, if the operator is at their machine,
    /// or the visitor simply left) - the visitor's own realistic patience, not a measured number, is
    /// what actually bounds this value, and `26-18`'s real delivery-latency measurement is the trigger
    /// to revisit it once real data exists (the identical "starting point, not a measured number"
    /// caveat `Ago.Chat.Module.Channels.ChannelResiliencePipelines`' own resilience defaults carry).</para>
    /// </summary>
    public static readonly TimeSpan RecommendedTimeToLive = TimeSpan.FromMinutes(5);
}

/// <summary>
/// The terminal result of one <see cref="IPushSender.SendAsync"/> call - `push-notifications.md`'s own
/// "The port, and what crosses it" table, `Delivered | TokenGone(reason) | TransientFailure(reason)`,
/// made concrete the same way <see cref="ChargeStoredPaymentMethodResult"/> already is for a payment.
/// </summary>
public abstract record PushSendOutcome
{
    private PushSendOutcome()
    {
    }

    public sealed record Delivered : PushSendOutcome;

    /// <summary>RuStore's own `400 INVALID_ARGUMENT` (a malformed push token) or `404 NOT_FOUND` (a
    /// valid token that has expired) - the two outcomes `26-05`'s own handler revokes
    /// <see cref="Domain.OperatorDevice"/> for, and the only two.</summary>
    public sealed record TokenGone(string Reason) : PushSendOutcome;

    /// <summary>Every other definitive answer RuStore gave: `401 UNAUTHORIZED`, `403 PERMISSION_DENIED`
    /// (both are *our own credential's* fault - `26-04`'s own backlog item is explicit that getting
    /// either backwards would empty the device table the first time the service token was ever wrong),
    /// `429 TOO_MANY_REQUESTS` and `500 INTERNAL` (RuStore's own outage or overload, nothing about any
    /// one device). `26-05`'s handler records <see cref="Domain.OperatorDevice.LastFailureAt"/> for this
    /// outcome and revokes nothing.</summary>
    public sealed record TransientFailure(string Reason) : PushSendOutcome;
}
