using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `13-03`: reacts to <see cref="OperatorRemovedFromSite"/> (`RemoveOperatorHandler`'s own outbox row)
/// by releasing the removed operator's `Assigned` conversations back to `Waiting`, reusing
/// <see cref="OperatorConversationReleaser"/>'s existing release logic verbatim - the same mechanism
/// `4-04`'s disconnect grace consumer and this item's own removal path both need, "an operator with no
/// business holding these conversations any more". `Competing`, matching every other consumer here -
/// exactly one `Worker` replica needs to act per removal.
///
/// <para>No idempotency ledger (`adr/0020`): a redelivered <see cref="OperatorRemovedFromSite"/> just
/// releases again, harmless - <see cref="OperatorConversationReleaser.ReleaseAllAsync"/> only ever acts
/// on conversations still genuinely `Assigned` to this operator, so a conversation already released by
/// an earlier delivery is silently skipped, not released twice (the identical reasoning
/// <c>OperatorDisconnectGraceConsumer</c>'s own remarks give for the same releaser).</para>
///
/// <para>`26-03`/`adr/0179` §1: gains one more call - <see cref="OperatorDeviceRevoker.RevokeAllAsync"/>
/// revokes every device row belonging to the removed operator, the third of that design's three
/// revocation causes ("the operator was removed from the site"). Idempotent the same way the release
/// above is: <see cref="OperatorDevice.Revoke"/> is a no-op on an already-revoked row, so a redelivery
/// revokes nothing a second time in any way that matters.</para>
/// </summary>
public sealed class OperatorRemovedConsumer(
    IEventConsumer consumer,
    OperatorConversationReleaser releaser,
    OperatorDeviceRevoker deviceRevoker,
    IOptions<OperatorRemovedConsumerOptions> options,
    ILogger<OperatorRemovedConsumer> logger) : BackgroundService
{
    private const string ConsumerName = "operator-removed";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        return consumer.SubscribeAsync(
            nameof(OperatorRemovedFromSite), SubscriptionMode.Competing, ConsumerName, retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<OperatorRemovedFromSite>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {nameof(OperatorRemovedFromSite)} payload for outbox message {envelope.MessageId}.");

            var operatorId = new OperatorId(contract.OperatorId);
            var released = await releaser.ReleaseAllAsync(operatorId, cancellationToken);
            if (released > 0)
            {
                logger.LogInformation(
                    "Operator {OperatorId} was removed - released {Count} assigned conversation(s) back to Waiting.",
                    operatorId.Value, released);
            }

            // `26-03`/`adr/0179` §1: the third revocation cause - a removed operator's phone must stop
            // receiving pushes about this tenant, unconditionally (unlike the release above, this has
            // no legitimate "nothing to do" reason to skip beyond "this operator registered no device").
            var revokedDevices = await deviceRevoker.RevokeAllAsync(operatorId, cancellationToken);
            if (revokedDevices > 0)
            {
                logger.LogInformation(
                    "Operator {OperatorId} was removed - revoked {Count} device(s).", operatorId.Value, revokedDevices);
            }

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to process operator removal for {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
