using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Platform.Kernel;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `23-88`/`adr/0093`: chat's own half of the async impact-preview round trip - reacts to whichever
/// module's own reply to <c>Ago.Chat.Contracts.ModuleQuantityImpactRequested</c>, over a topic no
/// module product publishes to yet (<see cref="ModuleQuantityImpactComputedWireContract"/>'s own
/// remarks state the honest, current status). One topic serves every module's own answer, the
/// identical "chat has no registry of modules to fan out by" shape
/// <c>Ago.Calendar.Worker.ModuleQuantityGrantedConsumer</c>'s own remarks describe for the opposite
/// direction - unlike that consumer, this one needs no module-key filter at all: it does not know or
/// care which module answered, only which (site, module) row to write the answer into, and the store
/// itself resolves that from the payload alone.
///
/// <para><b>No idempotency ledger</b> - the same reasoning <c>OperatorRemovedConsumer</c>'s own
/// remarks give for its own sibling: <see cref="IModuleQuantityImpactPreviewStore.AnswerAsync"/> is
/// naturally idempotent under at-least-once redelivery (a repeated identical answer just rewrites the
/// identical values) and naturally safe against a stale one (an answer to a question this row has
/// since moved past is silently discarded, that method's own remarks) - an inbox row here would
/// protect against nothing an ordinary re-application does not already handle correctly.</para>
/// </summary>
public sealed class ModuleQuantityImpactComputedConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<ModuleQuantityImpactComputedConsumerOptions> options,
    ILogger<ModuleQuantityImpactComputedConsumer> logger) : BackgroundService
{
    private const string ConsumerName = "module-quantity-impact-computed";

    // A literal, not `nameof(...)`: the topic name is whichever module product answers this
    // question's own choice of type name, a type this project cannot reference - the identical
    // reasoning ModuleQuantityGrantedConsumer's own topic constant gives for the opposite direction.
    private const string Topic = "ModuleQuantityImpactComputed";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        return consumer.SubscribeAsync(Topic, SubscriptionMode.Competing, ConsumerName, retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<ModuleQuantityImpactComputedWireContract>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {Topic} payload for outbox message {envelope.MessageId}.");

            await using var scope = scopeFactory.CreateAsyncScope();
            var previews = scope.ServiceProvider.GetRequiredService<IModuleQuantityImpactPreviewStore>();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();

            var siteId = new SiteId(contract.SiteId);
            var moduleKey = new ModuleKey(contract.ModuleKey);

            await previews.AnswerAsync(
                siteId, moduleKey, contract.RequestedQuantity, contract.AffectedCount,
                contract.AffectedItemDisplayNames, clock.UtcNow, cancellationToken);

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to process module quantity impact answer for {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
