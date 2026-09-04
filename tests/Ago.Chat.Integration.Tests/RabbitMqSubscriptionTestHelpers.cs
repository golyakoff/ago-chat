using System.Net;
using System.Text.Json;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `15-17`: replaces a fixed <c>Task.Delay(500)</c> before publishing - eight copies of the same
/// "both subscriptions to actually land" guess - with a wait for the fact a subscription actually
/// creates. `RabbitMqEventConsumer.StartAsync` (a <c>BackgroundService</c>) returns as soon as its
/// <c>ExecuteAsync</c> task is *started*, not once it *completes* - .NET's own
/// <c>BackgroundService.StartAsync</c> never awaits the task it kicks off, so `await consumer.StartAsync(...)`
/// races the real subscribe work, which is four steps in order: <c>QueueDeclareAsync</c> then
/// <c>QueueBindAsync</c> then the retry/dead-letter queue declares then <c>BasicConsumeAsync</c>.
///
/// <para><b>First version of this fix waited for the wrong fact.</b> It used
/// <c>QueueDeclarePassiveAsync</c> (mirroring `ago-platform`'s own `RabbitMqTestHelpers.QueueExistsAsync`,
/// `15-15`) - which succeeds as soon as step 1 alone has run. That is still a race: the wait can return
/// true in the window between the queue being declared and it being bound, publish into an exchange with
/// no bound queue yet, and have RabbitMQ silently discard the message - the exact `0/6` this item was
/// filed to fix, just with a narrower window (declare-to-bind instead of the whole subscribe) that only a
/// loaded CI runner was reliably slow enough to land in
/// (`golyakoff/ago-chat/actions/runs/33839119087`). Everything below now waits for step 4 - the consumer
/// actually attached - which implies the three steps before it, rather than for any one step in the
/// middle.</para>
///
/// <para><b>Competing</b> subscriptions (<see cref="WaitForCompetingSubscriptionAsync"/>):
/// `RabbitMqEventConsumer` names a `Competing` queue `{topic}.{consumerName}` - a name the test can
/// compute in advance - so this asks the RabbitMQ management API (`rabbitmq:4-management`, the image
/// both fixtures already use) for that queue's own `consumers` count and waits for it to reach at least
/// one. A binding-only check (`QueueBindAsync` having run, step 2) would already close the reported
/// `0/6` window and is *safer* than the original passive-declare check, but it still races step 4: a
/// bound queue with no consumer attached *holds* messages rather than losing them, so it would not
/// itself reproduce a failure - but it is not the fact this test actually needs, which is "will a
/// publish right now be picked up," and the consumer count says that directly and is no harder to
/// state. Not the AMQP-only `QueueDeclarePassiveAsync` this class used before: passive declare cannot
/// see a bind or a consumer at all, only the queue's own existence (step 1).</para>
///
/// <para><b>Broadcast</b> subscriptions (<see cref="WaitForNewBroadcastSubscriptionAsync"/>) cannot
/// compute a queue name in advance: `RabbitMqEventConsumer` names a `Broadcast` queue
/// `{topic}.{Guid.NewGuid()}` - generated inside the method the test never sees - and makes it
/// exclusive/auto-delete, so (unlike `Competing`) one is never left over from an earlier test either.
/// The management API's exchange-bindings listing names which queues are bound without needing to guess
/// - <see cref="GetQueueNamesBoundToExchangeAsync"/> is exactly `CountQueuesBoundToExchangeAsync`'s own
/// former job, still watching step 2 (a real binding, not merely a declared queue) - but naming the
/// queue, not just counting it, is what lets the wait go one step further and check *that* queue's own
/// `consumers` count too, the same step-4 fact the `Competing` case checks.</para>
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

    /// <summary>Reads a queue's own `consumers` field from the RabbitMQ management API - `null` when
    /// the queue does not exist yet (404: step 1 has not even happened), otherwise the live count of
    /// channels currently `BasicConsume`-ing on it. This is the step-4 fact every wait in this class is
    /// built from: a queue can exist (step 1), even be bound (step 2), and still lose a publish if
    /// nothing has attached to receive it yet.</summary>
    public static async Task<int?> GetQueueConsumerCountAsync(
        HttpClient management, string queueName, CancellationToken cancellationToken)
    {
        using var response = await management.GetAsync(
            $"/api/queues/%2F/{Uri.EscapeDataString(queueName)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        // The scalar `consumers` field is populated from RabbitMQ's periodic stats emission and is
        // genuinely absent - not zero, missing - for a few seconds right after a queue is created
        // (confirmed against a real `rabbitmq:4-management` container: a queue queried immediately
        // after PUT carries `consumer_details: []` but no `consumers` key at all; the key appears only
        // once a stats interval has elapsed). Reading it directly would throw on exactly the freshly-
        // declared, not-yet-consumed queues this wait exists to poll through, rather than politely
        // reporting "not yet." `consumer_details` is a live array of attached consumers, not a stats
        // snapshot, and is present from the moment the queue itself exists - its length is the same
        // fact without that timing dependency.
        return document.RootElement.GetProperty("consumer_details").GetArrayLength();
    }

    /// <summary>Polls <see cref="GetQueueConsumerCountAsync"/> until the named `Competing` queue has at
    /// least one attached consumer, or <paramref name="timeout"/> elapses - the per-subscription
    /// building block every `Competing` wait in this project's eight affected tests is built from.
    /// </summary>
    public static Task<bool> WaitForCompetingSubscriptionAsync(
        HttpClient management, string topic, string consumerName, TimeSpan timeout) =>
        OutboxTestHelpers.WaitUntilAsync(
            async () => await GetQueueConsumerCountAsync(management, CompetingQueueName(topic, consumerName), CancellationToken.None) is >= 1,
            timeout);

    /// <summary>The one-line-per-file replacement for the fixed <c>Task.Delay(500)</c> in six of the
    /// eight files this item fixes (the other two - <c>WebhookDispatchSharedQueueRegressionTests</c>/
    /// <c>WebhookDispatchIdempotencyTests</c> - assert per subscription individually instead, since
    /// each needs its own message naming the specific consumer that lost the race). Waits for every
    /// `(topic, consumerName)` pair's own `Competing` queue in turn - not in parallel - so a failure
    /// names the *specific* subscription that never landed rather than "one of these N did not."</summary>
    public static async Task AwaitAllCompetingSubscriptionsAsync(
        HttpClient management, TimeSpan timeout, params (string Topic, string ConsumerName)[] subscriptions)
    {
        foreach (var (topic, consumerName) in subscriptions)
        {
            var landed = await WaitForCompetingSubscriptionAsync(management, topic, consumerName, timeout);
            Assert.True(landed,
                $"The '{consumerName}' subscription to '{topic}' never landed - queue " +
                $"'{CompetingQueueName(topic, consumerName)}' never reached a live consumer within {timeout}.");
        }
    }

    /// <summary>Names every queue currently bound to <paramref name="exchange"/>, via the management
    /// API's exchange-bindings listing - the `Broadcast` case's only way to discover a queue name it was
    /// never told (see class remarks). Returns an empty set, not a thrown exception, when the exchange
    /// itself does not exist yet (404) - `RabbitMqEventConsumer.SubscribeAsync` declares the exchange
    /// before binding any queue to it, so "not there yet" is an expected early state, not a failure.
    /// </summary>
    public static async Task<IReadOnlySet<string>> GetQueueNamesBoundToExchangeAsync(
        HttpClient management, string exchange, CancellationToken cancellationToken)
    {
        using var response = await management.GetAsync(
            $"/api/exchanges/%2F/{Uri.EscapeDataString(exchange)}/bindings/source", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new HashSet<string>();
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.EnumerateArray()
            .Where(binding => binding.GetProperty("destination_type").GetString() == "queue")
            .Select(binding => binding.GetProperty("destination").GetString()!)
            .ToHashSet();
    }

    /// <summary>Polls until a queue bound to <paramref name="exchange"/> that was not already bound at
    /// <paramref name="queueNamesBeforeStart"/> (captured before the `Broadcast` consumer was started)
    /// has at least one attached consumer, or <paramref name="timeout"/> elapses. A captured baseline
    /// of names, not a bare count, matters twice over: `ConnectionFanoutFixture` is a collection
    /// fixture whose one RabbitMQ container (and therefore this exchange) is shared for the lifetime
    /// of every test class in the collection, so an unrelated queue may already be bound by the time
    /// this test starts - and naming the specific new queue is what lets this check that queue's own
    /// consumer count (step 4) instead of stopping at "a new binding exists" (step 2,
    /// <see cref="GetQueueNamesBoundToExchangeAsync"/>'s own former job as
    /// `CountQueuesBoundToExchangeAsync` - safer than the original passive-declare mistake, but still
    /// one step short of the fact this test actually needs).</summary>
    public static Task<bool> WaitForNewBroadcastSubscriptionAsync(
        HttpClient management, string exchange, IReadOnlySet<string> queueNamesBeforeStart, TimeSpan timeout) =>
        OutboxTestHelpers.WaitUntilAsync(
            async () =>
            {
                var currentQueueNames = await GetQueueNamesBoundToExchangeAsync(management, exchange, CancellationToken.None);
                foreach (var queueName in currentQueueNames)
                {
                    if (queueNamesBeforeStart.Contains(queueName))
                    {
                        continue;
                    }

                    if (await GetQueueConsumerCountAsync(management, queueName, CancellationToken.None) is >= 1)
                    {
                        return true;
                    }
                }

                return false;
            },
            timeout);
}
