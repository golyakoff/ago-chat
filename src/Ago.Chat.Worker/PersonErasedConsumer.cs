using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ErasePerson;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `adr/0189`/`26-275` slice #3: the calendar erased its own half of a person (Option A, issue 1815) -
/// its `PersonRecord` and their held past events, anonymising every event this person ever touched rather
/// than deleting it (`adr/0189`) - and published `PersonErased` for chat to erase the rest: the Person
/// itself, its conversations, its messages and everything under them (`docs/backlog/26-275-*.md` §2.1
/// step 2). This consumer only flags that cascade; <see cref="PersonErasureJob"/> does the actual
/// bounded, ordered removal off its own timer, reusing <see cref="ConversationErasureJob"/> for every
/// conversation exactly the way <c>SiteErasureJob</c> already reuses it for a whole site.
///
/// <para><b>Idempotent (CLAUDE.md rule 5) and no permission check.</b> Every outcome
/// <see cref="ErasePersonHandler"/> can report acks: a first-time flag is the ordinary case, an
/// already-flagged person is a redelivery or a sweep already under way, and an unknown person - never
/// registered here, or already fully erased by an earlier delivery - is a fact to log, since no retry
/// changes it. This is a trusted internal event between two products, the identical posture
/// <see cref="PersonRegisteredConsumer"/>'s own remarks state for the opposite direction: no
/// <see cref="Permission.CustomerErase"/> check belongs here, that gate already ran on the calendar's own
/// erase-initiation endpoint before this event was ever published.</para>
///
/// <para>The topic is a literal, not <c>nameof(...)</c>, for the identical reason
/// <see cref="PersonRegisteredConsumer"/>'s own remarks give: it is the module product's own type name, a
/// type this project cannot reference.</para>
/// </summary>
public sealed class PersonErasedConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<PersonErasedConsumerOptions> options,
    ILogger<PersonErasedConsumer> logger) : BackgroundService
{
    private const string ConsumerName = "person-erased";

    private const string Topic = "PersonErased";

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
            var contract = JsonSerializer.Deserialize<PersonErasedWireContract>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {Topic} payload for outbox message {envelope.MessageId}.");

            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<ErasePersonHandler>();

            var outcome = await handler.HandleAsync(
                new ErasePerson(new SiteId(contract.AccountId), new VisitorId(contract.PersonId), contract.OccurredAt),
                cancellationToken);

            if (outcome == PersonErasureOutcome.UnknownPerson)
            {
                // Not an error to retry: this account holds no such person - never registered here, or
                // already fully erased by an earlier delivery. Logged so either case stays visible.
                logger.LogWarning(
                    "PersonErased {PersonId} for account {AccountId} names a person this deployment does not hold; skipped.",
                    contract.PersonId, contract.AccountId);
            }

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to flag person for erasure for outbox message {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
