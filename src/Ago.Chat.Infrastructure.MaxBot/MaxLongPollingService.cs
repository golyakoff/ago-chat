using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ReceiveChannelMessage;
using Ago.Chat.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Infrastructure.MaxBot;

/// <summary>
/// `14-02`: the local/dev inbound mechanism MAX's own documentation calls long polling suitable for -
/// "limited by speed and event retention", the mirror of the production webhook receiver
/// (`Ago.Chat.Api`'s <c>MaxWebhookEndpoints</c>). Both exist because this item's own backlog note found
/// MAX's API genuinely requires both: webhook and long polling are mutually exclusive on MAX's own side
/// (registering one implicitly means not using the other for that bot), and webhooks require a public
/// HTTPS endpoint with a trusted-CA certificate the local compose loop does not have - so this is not a
/// convenience, it is the only inbound path this item's own runbook verification can exercise without a
/// live token *and* a live public deployment at once.
///
/// <para><b>One polling loop per active MAX credential, started and stopped as credentials come and go.</b>
/// A single shared poll would have to multiplex MAX's own per-bot long-poll semantics (one marker, one
/// token, one HTTPS call per bot) into one loop - genuinely awkward, versus one lightweight
/// <see cref="Task"/> per bot, which is what MAX's own API shape (one token, one <c>GET /updates</c>
/// stream per bot) already is. <see cref="_pollers"/> is this class's only mutable shared state, guarded
/// by <see cref="_gate"/> because the outer refresh loop and a poller's own faulted-task cleanup can both
/// touch it.</para>
/// </summary>
public sealed class MaxLongPollingService(
    MaxApiClient client,
    IServiceScopeFactory scopeFactory,
    IOptions<MaxBotApiOptions> apiOptions,
    IOptions<MaxLongPollingServiceOptions> pollingOptions,
    ILogger<MaxLongPollingService> logger) : BackgroundService
{
    private readonly Dictionary<ChannelCredentialId, (CancellationTokenSource Cancellation, Task Loop)> _pollers = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RefreshPollersAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(pollingOptions.Value.CredentialRefreshIntervalSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Ordinary shutdown.
        }
        finally
        {
            await StopAllPollersAsync();
        }
    }

    private async Task RefreshPollersAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<ChannelCredential> active;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var credentials = scope.ServiceProvider.GetRequiredService<IChannelCredentialRepository>();
            active = await credentials.GetAllActiveAsync(ChannelKind.Max, stoppingToken);
        }

        var activeIds = active.Select(c => c.Id).ToHashSet();

        await _gate.WaitAsync(stoppingToken);
        try
        {
            // Stop polling a credential that is no longer active (revoked since the last refresh).
            foreach (var staleId in _pollers.Keys.Where(id => !activeIds.Contains(id)).ToList())
            {
                var (cancellation, loop) = _pollers[staleId];
                await CancelAndAwaitAsync(cancellation, loop);
                _pollers.Remove(staleId);
            }

            // Start polling a credential that just became active.
            foreach (var credential in active.Where(c => !_pollers.ContainsKey(c.Id)))
            {
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var loop = Task.Run(() => PollOneCredentialAsync(credential.Id, cancellation.Token), CancellationToken.None);
                _pollers[credential.Id] = (cancellation, loop);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>One bot's own long-poll loop. Decrypts the token once per iteration (never cached in
    /// memory across a revoke-and-replace) rather than once for the loop's whole lifetime, trading one
    /// extra AES-GCM decrypt per poll for never risking a stale token surviving past its own revocation
    /// - the loop's own refresh cadence already accepts up to <see cref="MaxLongPollingServiceOptions.CredentialRefreshIntervalSeconds"/>
    /// of staleness for *whether* to poll at all, so re-reading the token on every iteration costs
    /// nothing relative to that.</summary>
    private async Task PollOneCredentialAsync(ChannelCredentialId credentialId, CancellationToken cancellationToken)
    {
        long? marker = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                string token;
                SiteId siteId;
                await using (var scope = scopeFactory.CreateAsyncScope())
                {
                    var credentials = scope.ServiceProvider.GetRequiredService<IChannelCredentialRepository>();
                    var cipher = scope.ServiceProvider.GetRequiredService<IChannelCredentialCipher>();

                    var credential = await credentials.GetByIdAsync(credentialId, cancellationToken);
                    if (credential is null || !credential.Active)
                    {
                        // Revoked between this iteration starting and now - the next RefreshPollersAsync
                        // tick will remove this loop from _pollers; exiting now just stops it slightly
                        // sooner instead of making one more doomed HTTP call.
                        return;
                    }

                    token = cipher.Decrypt(credential.TokenCiphertext);
                    siteId = credential.SiteId;
                }

                var envelope = await client.GetUpdatesAsync(
                    token, marker, apiOptions.Value.LongPollTimeoutSeconds, cancellationToken);
                marker = envelope.Marker ?? marker;

                foreach (var update in envelope.Updates ?? [])
                {
                    await DispatchIfMessageAsync(siteId, update, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MAX long-poll for credential {ChannelCredentialId} failed; retrying.", credentialId.Value);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(pollingOptions.Value.ErrorBackoffSeconds), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task DispatchIfMessageAsync(SiteId siteId, MaxUpdate update, CancellationToken cancellationToken)
    {
        var parsed = MaxInboundMessageParser.TryParse(update);
        if (parsed is null)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ReceiveChannelMessageHandler>();

        var result = await handler.HandleAsync(
            new ReceiveChannelMessage(
                siteId, ChannelKind.Max,
                new ExternalChannelAddress(parsed.SenderId.ToString()),
                new ExternalMessageId(parsed.ExternalMessageId),
                parsed.Text),
            cancellationToken);

        if (result.IsFailure)
        {
            logger.LogWarning(
                "Could not receive a MAX message for site {SiteId}: {Code} {Message}",
                siteId.Value, result.Error!.Value.Code, result.Error!.Value.Message);
        }
    }

    private async Task StopAllPollersAsync()
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            foreach (var (cancellation, loop) in _pollers.Values)
            {
                await CancelAndAwaitAsync(cancellation, loop);
            }

            _pollers.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task CancelAndAwaitAsync(CancellationTokenSource cancellation, Task loop)
    {
        await cancellation.CancelAsync();
        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown/revoke.
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }
}
