using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.RegisterExternalPerson;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `adr/0184` decision 2: a module took a booking with no chat origin, minted a person id locally and
/// published <c>PersonRegistered</c>; this consumer creates the Person in the account's registry under
/// exactly that id. The counterpart of the <c>ContactCollected</c> flow `adr/0147` used to run the other
/// way - and the only direction left: chat never learns about a person from a module except through
/// this event, and a module never learns about a person from chat except by the id a chat-origin booking
/// already carries.
///
/// <para><b>Idempotent (CLAUDE.md rule 5) and never an overwrite</b> - <see cref="RegisterExternalPersonHandler"/>'s
/// own remarks. Every outcome acks: a created person is the ordinary case, an already-existing one is a
/// redelivery (or a visitor chat had in fact already seen), and an unknown account is a fact to log,
/// since no retry will make it appear. Only a genuine failure - the database unreachable, say - throws
/// into the retry policy.</para>
///
/// <para>The topic is a literal, not <c>nameof(...)</c>: it is the module product's own type name, a type
/// this project cannot reference - the identical reasoning every other module-side consumer's topic
/// constant gives.</para>
/// </summary>
public sealed class PersonRegisteredConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<PersonRegisteredConsumerOptions> options,
    ILogger<PersonRegisteredConsumer> logger) : BackgroundService
{
    private const string ConsumerName = "person-registered";

    private const string Topic = "PersonRegistered";

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
            var contract = JsonSerializer.Deserialize<PersonRegisteredWireContract>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {Topic} payload for outbox message {envelope.MessageId}.");

            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<RegisterExternalPersonHandler>();

            var outcome = await handler.HandleAsync(
                new RegisterExternalPerson(
                    new SiteId(contract.AccountId), new VisitorId(contract.PersonId), contract.Phone, contract.Name,
                    contract.OccurredAt),
                cancellationToken);

            if (outcome == PersonRegistrationOutcome.SiteUnknown)
            {
                // Not an error to retry: the account named does not exist in this deployment, and nothing
                // about waiting changes that. Logged so a misrouted module deployment is visible.
                logger.LogWarning(
                    "PersonRegistered {PersonId} names account {AccountId}, which this deployment does not hold; skipped.",
                    contract.PersonId, contract.AccountId);
            }

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to register person for outbox message {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
