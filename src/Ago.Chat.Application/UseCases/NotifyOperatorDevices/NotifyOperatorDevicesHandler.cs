using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.NotifyOperatorDevices;

/// <summary>
/// `26-05`/`push-notifications.md`'s own "Fan-out" section: the one place that decides whether an
/// operator's phone should buzz, and about what. `OperatorAssignmentPushConsumer`/
/// `OperatorMessagePushConsumer` (`Ago.Chat.Worker`) do nothing but deserialize, scope, call this
/// class, and ack - the same "the consumer holds no decision" split every other Worker fan-out in this
/// codebase already uses (`ConversationAssignmentFanoutConsumer`/`ResolveConversationAssignmentTargetsHandler`,
/// `ConnectionFanoutConsumer`/`ResolveMessageDeliveryTargetsHandler`).
///
/// <para><b>Reuses `ago-console/src/workspace/alerts.ts`'s own rules rather than inventing a second
/// set</b> (this item's own backlog scope, and `adr/0179`/`adr/0180` §2 before it): visitor messages
/// only, the assigned operator only, `alertTextFor`'s text shape with no message body. The English
/// strings below are `alertTextFor`'s own default-locale text, ported rather than duplicated in
/// spirit - there is no `Operator` locale to route on server-side (`Operator` carries no
/// language/locale field, unlike `ConsoleStrings`' own default-to-`en` fallback for the identical
/// "no caller-supplied locale" reason).</para>
///
/// <para><b>The server never suppresses on presence</b> (`adr/0179` §3, `push-notifications.md`'s own
/// "The question this design exists to answer") - stated here as what this class does <em>not</em>
/// depend on: neither this handler nor either consumer takes an <c>IConnectionRegistry</c> or an
/// <c>INodeFanoutPublisher</c> constructor parameter at all. There is no presence value in this
/// class's own dependency graph to consult, correctly or otherwise - the decision is architectural,
/// not a runtime check this class chooses not to make. <c>NotifyOperatorDevicesHandlerTests</c>' own
/// non-suppression test proves this concretely by seeding a connected presence entry in a real
/// connection registry alongside the call, not merely by this class's own missing constructor
/// parameter.</para>
///
/// <para><b>No `inbox` idempotency row</b> (`adr/0020`, `push-notifications.md`'s own "Idempotency,
/// without an inbox row"): a purely derived, best-effort notification computed from an already-
/// outboxed event may publish directly, and a redelivered event just re-sends the same, harmless
/// push - the client-side notification tag (<see cref="GroupKeyFor"/>) and message-id dedupe
/// (`26-18`) are what actually collapse a redelivery, not a database write here.</para>
/// </summary>
public sealed class NotifyOperatorDevicesHandler(
    IOperatorDeviceRepository devices, IConversationRepository conversations, IPushSenderResolver pushSenders, IClock clock,
    IPermissionChecker permissions)
{
    private const string ReasonAssigned = "assigned";
    private const string ReasonMessage = "message";

    /// <summary>`26-86`: the third kind - see <see cref="HandleWaitingAsync"/>'s own remarks. The three
    /// constants here are also, as of `26-81`, the only three values <see cref="SendToOperatorAsync"/>
    /// ever writes into <c>data["reason"]</c> on the wire - see that method's own remarks.</summary>
    private const string ReasonWaiting = "waiting";

    /// <summary>`ConversationAssignedToOperator` already names both the conversation and the operator -
    /// no load, the identical property `ResolveConversationAssignmentTargetsHandler` relies on for the
    /// realtime counterpart of this same event. Covers a transfer for free (`ConversationTransferredMapper`
    /// maps a transfer onto this same contract), so there is nothing transfer-specific here at all.</summary>
    public async Task<Result> HandleAssignmentAsync(NotifyOperatorDeviceForAssignment command, CancellationToken cancellationToken)
    {
        var who = ShortVisitorId(command.VisitorId);
        await SendToOperatorAsync(
            command.OperatorId,
            ReasonAssigned,
            title: "New conversation assigned",
            body: $"{who} is waiting for you.",
            groupKey: GroupKeyFor(command.ConversationId),
            data: new Dictionary<string, string> { ["conversationId"] = command.ConversationId.Value.ToString() },
            cancellationToken);

        return Result.Success();
    }

    /// <summary>
    /// `alerts.ts`'s own three filters, restated for the server: (1) `AuthorKind == Visitor` - an
    /// operator's own echoed-back send is not news, `decideAlert`'s console-side rule verbatim;
    /// (2) the conversation must have an assigned operator - `MessageAccepted` carries no operator, so
    /// the conversation is loaded, exactly as `ResolveMessageDeliveryTargetsHandler` already does for
    /// the same event; (3) that operator is the only recipient. Each non-send path records
    /// <see cref="ChatMetrics.RecordPushSuppressed"/> under its own <c>reason</c> tag - the number that
    /// tells "push is broken" apart from "nobody has ever registered a device"
    /// (`push-notifications.md`'s own words).
    /// </summary>
    public async Task<Result> HandleMessageAsync(NotifyOperatorDeviceForMessage command, CancellationToken cancellationToken)
    {
        if (!string.Equals(command.AuthorKind, nameof(MessageAuthorKind.Visitor), StringComparison.Ordinal))
        {
            ChatMetrics.RecordPushSuppressed("not_visitor");
            return Result.Success();
        }

        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null)
        {
            // Should not happen in practice: MessageAccepted is only published after the message's own
            // transaction committed (adr/0005), so the conversation it names is already durable by the
            // time this consumer sees it - the identical reasoning ConnectionFanoutConsumer's own
            // remarks give for the same event.
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        if (conversation.OperatorId is not { } operatorId)
        {
            ChatMetrics.RecordPushSuppressed("unassigned");
            return Result.Success();
        }

        var who = ShortVisitorId(conversation.VisitorId);
        await SendToOperatorAsync(
            operatorId,
            ReasonMessage,
            title: "New message",
            body: $"{who} sent a message.",
            groupKey: GroupKeyFor(command.ConversationId),
            data: new Dictionary<string, string>
            {
                ["conversationId"] = command.ConversationId.Value.ToString(),
                ["messageId"] = command.MessageId.Value.ToString(),
            },
            cancellationToken);

        return Result.Success();
    }

    /// <summary>
    /// `26-86`: the third arm - a brand-new conversation entered `Waiting` with nobody assigned at all,
    /// so unlike the two arms above there is no single recipient to resolve. Every non-removed operator
    /// on <see cref="NotifyOperatorDeviceForWaiting.SiteId"/> who holds `Permission.ConversationRead` is
    /// a recipient (this item's own backlog scope: "every operator on the site who holds
    /// conversation:read", not one assignee - there is no assignee yet) - resolved through
    /// <see cref="IPermissionChecker.ListNonRemovedHolderIdsAsync"/> rather than a new query, the same
    /// role-based resolution `PermissionChecker.CountNonRemovedHoldersAsync` already gives
    /// `RemoveOperatorHandler`'s own last-manager guard, restated as a list of ids instead of a count.
    ///
    /// <para>No conversation load, for the identical reason <see cref="HandleAssignmentAsync"/>'s own
    /// remarks state: `ConversationWaitingForOperator` already names the conversation, site and visitor
    /// directly.</para>
    ///
    /// <para>Whether a plain Operator role (holds `conversation:send` but not `conversation:assign`)
    /// can act on this notification is explicitly left open by this item's own backlog - this handler
    /// does not gate on `conversation:assign` at all, only `conversation:read`, per that same scope
    /// note ("do not hide the notification... from a plain Operator as part of this item").</para>
    /// </summary>
    public async Task<Result> HandleWaitingAsync(NotifyOperatorDeviceForWaiting command, CancellationToken cancellationToken)
    {
        var operatorIds = await permissions.ListNonRemovedHolderIdsAsync(
            command.SiteId, Permission.ConversationRead, cancellationToken);
        if (operatorIds.Count == 0)
        {
            // Distinct from "no_devices" below: nobody on this site is even eligible to be notified,
            // as opposed to an eligible operator who simply has no registered device.
            ChatMetrics.RecordPushSuppressed("no_eligible_operators");
            return Result.Success();
        }

        var who = ShortVisitorId(command.VisitorId);
        var data = new Dictionary<string, string> { ["conversationId"] = command.ConversationId.Value.ToString() };
        foreach (var operatorId in operatorIds)
        {
            await SendToOperatorAsync(
                operatorId,
                ReasonWaiting,
                title: "New conversation waiting",
                body: $"{who} is waiting for an operator.",
                groupKey: GroupKeyFor(command.ConversationId),
                data,
                cancellationToken);
        }

        return Result.Success();
    }

    /// <summary>`26-81`: <paramref name="data"/> arrives from each of the three arms above carrying only
    /// its own domain keys (<c>conversationId</c>, and <c>messageId</c> for the message kind) - the
    /// explicit `reason` key every kind now puts on the wire is added exactly once, here, rather than at
    /// each of the three call sites, so there is exactly one place that can forget it.</summary>
    private async Task SendToOperatorAsync(
        OperatorId operatorId, string reason, string title, string body, string groupKey,
        IReadOnlyDictionary<string, string> data, CancellationToken cancellationToken)
    {
        var activeDevices = await devices.ListActiveForOperatorAsync(operatorId, cancellationToken);
        if (activeDevices.Count == 0)
        {
            ChatMetrics.RecordPushSuppressed("no_devices");
            return;
        }

        var wireData = new Dictionary<string, string>(data) { ["reason"] = reason };

        // `push-notifications.md`'s own "The port, and what crosses it": TimeToLive is
        // PushMessage.RecommendedTimeToLive, this item's own only caller of that value.
        foreach (var device in activeDevices)
        {
            var message = new PushMessage(
                device.Token, title, body, groupKey, PushMessage.RecommendedTimeToLive, wireData);
            var providerTag = device.Provider.ToString().ToLowerInvariant();

            // `26-100`/`adr/0181`: route this one device to the sender for its own transport. FCM
            // (primary) and RuStore (fallback) devices sit side by side in the same operator's list, so
            // the selection is per device, not per fan-out - the resolver's whole reason to exist
            // (IPushSenderResolver's own remarks). A device written before FCM existed carries RuStore
            // and resolves to the unchanged RuStore adapter, which is why this change is a no-op for
            // every existing row.
            var pushSender = pushSenders.Resolve(device.Provider);

            // Deliberately not caught here: IPushSender.SendAsync's own remarks state that only a
            // response it cannot parse into a definitive provider answer at all - a network failure, a
            // timeout - is ever thrown, and that is exactly the "the provider or the network failed"
            // case `push-notifications.md`'s own "Quiet failure" section wants to reach the DLQ rather
            // than be swallowed here: the consumer rethrows, and the message lands in
            // operator-assignment-push.dlq/operator-message-push.dlq for the same reason
            // ChannelMessageDeliveryConsumer's own unreachable-provider case does.
            var outcome = await pushSender.SendAsync(message, cancellationToken);

            switch (outcome)
            {
                case PushSendOutcome.Delivered:
                    ChatMetrics.RecordPushSend(reason, providerTag, "delivered");
                    break;

                case PushSendOutcome.TokenGone:
                    ChatMetrics.RecordPushSend(reason, providerTag, "token_gone");
                    device.Revoke(clock.UtcNow);
                    await devices.SaveAsync(device, cancellationToken);
                    ChatMetrics.RecordPushTokenRevoked("provider_unregistered");
                    break;

                case PushSendOutcome.TransientFailure transientFailure:
                    // Never a device fault (IPushSender.SendAsync's own remarks) - recorded on the row
                    // for diagnostics, never revoked. The next send for this device is retried exactly
                    // as before, unaffected by this one credential-or-provider-side failure.
                    ChatMetrics.RecordPushSend(reason, providerTag, "failed");
                    device.RecordSendFailure(transientFailure.Reason, clock.UtcNow);
                    await devices.SaveAsync(device, cancellationToken);
                    break;
            }
        }
    }

    /// <summary>`push-notifications.md`'s own "Idempotency, without an inbox row": the identical
    /// `ago-conversation-{conversationId}` value `useAlerts.ts` already uses as its `Notification`
    /// `tag`, carried here in <see cref="PushMessage.GroupKey"/> even though RuStore's own send API has
    /// no collapse-key field to put it in - the *client* still collapses on it (`26-18`), and this is
    /// how it gets there (`RuStorePushSender.BuildData`'s own remarks fold it into the wire `data`
    /// map, under the `groupKey` key, alongside <c>conversationId</c>/<c>messageId</c>).</summary>
    private static string GroupKeyFor(ConversationId conversationId) => $"ago-conversation-{conversationId.Value}";

    /// <summary>`alertTextFor`'s own `${strings.alertVisitorPrefix} ${visitorId.slice(0, 8)}` - both
    /// events this handler ever fires for carry a real <see cref="VisitorId"/> (the assignment contract
    /// names one directly; the message path loads the conversation, which always has one), so unlike
    /// the console's own function this never has an unknown-visitor branch to fall back to.</summary>
    private static string ShortVisitorId(VisitorId visitorId) => $"Visitor {visitorId.Value.ToString()[..8]}";
}
