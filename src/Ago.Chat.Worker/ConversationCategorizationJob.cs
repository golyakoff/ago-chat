using Ago.Chat.Application.UseCases.CategorizeConversation;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `19-02`/`adr/0078`'s kind 2: periodically assigns each recently-closed, still-untagged conversation
/// zero or more of its own site's existing tags, through `CategorizeConversationHandler`
/// (<see cref="Domain.TagSource.Ai"/>) - never a request path a visitor or operator waits on
/// (this item's own Scope: "a periodic batch job, not real-time classification"). Same
/// `PeriodicTimer`/`BackgroundService` shape as `AutoCloseInactiveConversationsJob` - runs once
/// immediately, then every <see cref="ConversationCategorizationJobOptions.Interval"/>, and a transient
/// failure logs and retries next cycle rather than killing the sweep (`concurrency.md`).
///
/// <para><b>Why a fresh <see cref="IServiceScopeFactory"/> scope per conversation, not per tick.</b>
/// The identical reasoning <see cref="AutoCloseInactiveConversationsJob"/>'s own remarks give: this
/// class is a singleton hosted service, but <see cref="CategorizeConversationHandler"/> is scoped - one
/// scope per candidate keeps each classification's own reads (this conversation's history, this site's
/// tag vocabulary) as isolated as a real request would get them, rather than sharing one
/// <c>DbContext</c>/change tracker across an entire batch.</para>
/// </summary>
public sealed class ConversationCategorizationJob(
    NpgsqlDataSource dataSource,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IOptions<ConversationCategorizationJobOptions> options,
    ILogger<ConversationCategorizationJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // concurrency.md: a BackgroundService catches and continues - a transient Postgres or
                // provider blip here must not permanently kill the categorization sweep.
                logger.LogError(ex, "Conversation categorization cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken)); // runs once immediately, then every Interval
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow - options.Value.LookbackWindow;

        IReadOnlyList<(ConversationId ConversationId, SiteId SiteId)> candidates;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            candidates = await ConversationCategorizationQuery.FindUncategorizedClosedBatchAsync(
                connection, cutoff, options.Value.BatchSize, cancellationToken);
        }

        var tagged = 0;
        foreach (var (conversationId, siteId) in candidates)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<CategorizeConversationHandler>();
            var result = await handler.HandleAsync(new CategorizeConversation(conversationId, siteId), cancellationToken);

            if (result.IsFailure)
            {
                // The candidate no longer exists - vanishingly unlikely (nothing in this codebase
                // deletes a Conversation row), logged rather than treated as a sweep-ending error, the
                // same "normal outcome to log and move on" shape AutoCloseInactiveConversationsJob's
                // own remarks give its own stale-candidate case.
                logger.LogDebug(
                    "Categorization skipped for conversation {ConversationId}: {ErrorCode}.",
                    conversationId.Value, result.Error!.Value.Code);
                continue;
            }

            if (result.Value == CategorizationOutcome.Tagged)
            {
                tagged++;
            }
        }

        if (tagged > 0)
        {
            // `19-02`'s own Done-when: a log line distinguishable from an operator's own tagging
            // action, the same "the console/log should say who did this" reasoning
            // AutoCloseInactiveConversationsJob's own remarks give for its own auto-close log line.
            logger.LogInformation(
                "AI-tagged {Count} conversation(s) in this cycle, out of {CandidateCount} candidate(s).",
                tagged, candidates.Count);
        }
    }
}
