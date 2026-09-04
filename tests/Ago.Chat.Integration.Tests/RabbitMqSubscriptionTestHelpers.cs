using System.Net;
using System.Text.Json;
using Ago.Platform.Messaging.RabbitMq;
using RabbitMQ.Client.Exceptions;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `15-17`: replaces a fixed <c>Task.Delay(500)</c> before publishing - eight copies of the same
/// "both subscriptions to actually land" guess - with a wait for the fact a subscription actually
/// creates. `RabbitMqEventConsumer.StartAsync` (a <c>BackgroundService</c>) returns as soon as its
/// <c>ExecuteAsync</c> task is *started*, not once it *completes* - .NET's own
/// <c>BackgroundService.StartAsync</c> never awaits the task it kicks off, so `await consumer.StartAsync(...)`
/// races the real subscribe work (exchange declare -&gt; queue declare -&gt; queue bind -&gt;
/// <c>BasicConsumeAsync</c>) rather than waiting for it. A fixed sleep guesses how long that race
/// takes; this instead asks the broker directly whether the queue the subscription would have bound
/// is there yet.
///
/// <para><b>Competing</b> subscriptions (<see cref="QueueExistsAsync"/>): `RabbitMqEventConsumer`
/// names a `Competing` queue `{topic}.{consumerName}` - a name the test can compute in advance, so a
/// passive declare on that exact name is enough. Mirrors `ago-platform`'s own
/// `RabbitMqTestHelpers.QueueExistsAsync` (`15-15`, on branch `fix/15-15-ephemeral-queues` and not yet
/// on `main` at the time of writing) - copied rather than referenced, because this repository restores
/// `Ago.Platform.*` from a NuGet package (`nuget.config`'s local feed), not a project reference, and a
/// platform test project's own internal helper is never packed into that feed; taking a project
/// reference across the package boundary just for a five-line test helper would also invert
/// `adr/0012`'s "products depend on the platform, never the reverse" for something this small. If
/// `15-15` lands on `main` and a later `ago-platform` bump exposes the helper publicly, this copy
/// should be deleted in favour of that one - a follow-up, not done here, since this item's own scope is
/// `ago-chat` only (`15-17`'s brief).</para>
///
/// <para><b>Broadcast</b> subscriptions (<see cref="CountQueuesBoundToExchangeAsync"/>) cannot use the
/// same trick: `RabbitMqEventConsumer` names a `Broadcast` queue `{topic}.{Guid.NewGuid()}` - generated
/// inside the method the test never sees - and makes it exclusive/auto-delete, so (unlike `Competing`)
/// it is never left over from an earlier test either; there is no name to passively declare. The only
/// fact observable from outside is "how many queues are now bound to this topic's exchange", which
/// needs the RabbitMQ management HTTP API (`rabbitmq:4-management`, the image both fixtures already
/// use) - plain AMQP 0-9-1 has no "list bindings" operation.</para>
/// </summary>
internal static class RabbitMqSubscriptionTestHelpers
{
    /// <summary>`Ago.Platform.Realtime.NodeDeliveryConsumer`'s own consumer name - a literal
    /// hardcoded directly inside that class's `ExecuteAsync` (`ago-platform`, not this repository),
    /// with no `internal`/`InternalsVisibleTo` option available across the NuGet package boundary the
    /// way `ConnectionFanoutConsumer.ConsumerName` and its siblings now offer (see those classes' own
    /// `15-17` remarks). Centralized here once, rather than retyped in every one of the five files
    /// that start a `NodeDeliveryConsumer`, so a future rename on the platform side has exactly one
    /// place in this repository to catch up, not five silently-drifting copies.</summary>
    public const string NodeDeliveryConsumerName = "node-delivery";

    /// <summary>The exact formula `RabbitMqEventConsumer.SubscribeAsync` uses for a `Competing`
    /// queue's name (`Ago.Platform.Messaging.RabbitMq`) - duplicated here for the same reason a topic
    /// name is already duplicated as `nameof(SomeContract)` across every one of these tests: there is
    /// no shared symbol across the package boundary to derive it from instead.</summary>
    public static string CompetingQueueName(string topic, string consumerName) => $"{topic}.{consumerName}";

    /// <summary>Mirrors `ago-platform`'s own `RabbitMqTestHelpers.QueueExistsAsync` (`15-15`) - see
    /// this class's own remarks for why it is copied rather than referenced. A passive declare succeeds
    /// silently when the queue exists and closes the channel it ran on when it does not, so this always
    /// opens a fresh channel and never reuses one a failed declare has already closed.
    ///
    /// Two different failure codes both close the channel, and only one of them means "gone": 404
    /// (NOT_FOUND) does, but 405 (RESOURCE_LOCKED - "cannot obtain exclusive access to locked queue")
    /// means the queue is exclusive to a *different* connection and very much still exists. No
    /// `Competing` queue any of these eight tests declares is exclusive, so 405 is not expected in
    /// practice here - kept anyway for parity with the helper this is copied from, so a future
    /// exclusive/`Competing` queue does not silently misreport as absent.</summary>
    public static async Task<bool> QueueExistsAsync(RabbitMqConnection connection, string queueName)
    {
        var channel = await connection.CreateChannelAsync();
        try
        {
            await channel.QueueDeclarePassiveAsync(queueName);
            return true;
        }
        catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 404)
        {
            return false;
        }
        catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 405)
        {
            return true;
        }
        finally
        {
            await channel.DisposeAsync();
        }
    }

    /// <summary>Polls <see cref="QueueExistsAsync"/> until the named `Competing` queue exists or
    /// <paramref name="timeout"/> elapses - the per-subscription building block every `Competing` wait
    /// in this project's eight affected tests is built from.</summary>
    public static Task<bool> WaitForCompetingSubscriptionAsync(
        RabbitMqConnection probeConnection, string topic, string consumerName, TimeSpan timeout) =>
        OutboxTestHelpers.WaitUntilAsync(
            () => QueueExistsAsync(probeConnection, CompetingQueueName(topic, consumerName)), timeout);

    /// <summary>The one-line-per-file replacement for the fixed <c>Task.Delay(500)</c> in six of the
    /// eight files this item fixes (the other two - <c>WebhookDispatchSharedQueueRegressionTests</c>/
    /// <c>WebhookDispatchIdempotencyTests</c> - assert per subscription individually instead, since
    /// each needs its own message naming the specific consumer that lost the race). Waits for every
    /// `(topic, consumerName)` pair's own `Competing` queue in turn - not in parallel - so a failure
    /// names the *specific* subscription that never landed rather than "one of these N did not."</summary>
    public static async Task AwaitAllCompetingSubscriptionsAsync(
        RabbitMqConnection probeConnection, TimeSpan timeout, params (string Topic, string ConsumerName)[] subscriptions)
    {
        foreach (var (topic, consumerName) in subscriptions)
        {
            var landed = await WaitForCompetingSubscriptionAsync(probeConnection, topic, consumerName, timeout);
            Assert.True(landed,
                $"The '{consumerName}' subscription to '{topic}' never landed - no queue named " +
                $"'{CompetingQueueName(topic, consumerName)}' was bound within {timeout}.");
        }
    }

    /// <summary>The `Broadcast` case <see cref="QueueExistsAsync"/> cannot cover (see class remarks) -
    /// counts how many queues are currently bound to <paramref name="exchange"/> via the management
    /// API. Returns 0, not a thrown exception, when the exchange itself does not exist yet (404 from
    /// the management API) - `RabbitMqEventConsumer.SubscribeAsync` declares the exchange before
    /// binding any queue to it, so "not there yet" is an expected early state, not a failure.</summary>
    public static async Task<int> CountQueuesBoundToExchangeAsync(
        HttpClient management, string exchange, CancellationToken cancellationToken)
    {
        using var response = await management.GetAsync(
            $"/api/exchanges/%2F/{Uri.EscapeDataString(exchange)}/bindings/source", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return 0;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.GetArrayLength();
    }

    /// <summary>Polls <see cref="CountQueuesBoundToExchangeAsync"/> until at least one *new* queue
    /// (beyond <paramref name="bindingsBeforeStart"/>, a count taken before the `Broadcast` consumer
    /// was started) is bound, or <paramref name="timeout"/> elapses. Counting from a captured
    /// baseline - not asserting an absolute count - matters because `ConnectionFanoutFixture` is a
    /// collection fixture: its one RabbitMQ container, and therefore this exchange, is shared for the
    /// lifetime of every test class in the collection, so an unrelated queue from an earlier test may
    /// already be bound by the time this one starts.</summary>
    public static Task<bool> WaitForNewBroadcastSubscriptionAsync(
        HttpClient management, string exchange, int bindingsBeforeStart, TimeSpan timeout) =>
        OutboxTestHelpers.WaitUntilAsync(
            async () => await CountQueuesBoundToExchangeAsync(management, exchange, CancellationToken.None) > bindingsBeforeStart,
            timeout);
}
