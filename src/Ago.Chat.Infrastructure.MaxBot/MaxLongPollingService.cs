using System.Net;
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
///
/// <para><b><c>14-16</c>/<c>adr/0089</c>: one poll loop per credential across the whole fleet, not just
/// within this process.</b> Every entry in <see cref="_pollers"/> is a <em>candidate</em> loop, not a
/// guaranteed one - <see cref="PollOneCredentialAsync"/> claims <see cref="IChannelPollerOwnership"/>'s
/// lease for its credential first and returns immediately, without polling anything, if another Worker
/// process already holds it. <see cref="RefreshPollersAsync"/>'s reap step is what turns "returned
/// immediately" back into a retry: a loop that ends - lease denied, lease lost mid-poll
/// (<see cref="ChannelPollerLeaseLostException"/>), or genuinely stopped - is removed from
/// <see cref="_pollers"/> so the next tick starts a fresh attempt for it, exactly as if it had never
/// been active. This is the entire mechanism; nothing in this class knows or needs to know that the
/// lease is a PostgreSQL advisory lock underneath - see <c>TelegramLongPollingService</c>'s identical
/// treatment, `14-16`'s own backlog note on why both channels get it in the same change.</para>
/// </summary>
public sealed class MaxLongPollingService(
    MaxApiClient client,
    IServiceScopeFactory scopeFactory,
    IChannelPollerOwnership pollerOwnership,
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
            // `14-16`/`adr/0089`: reap a loop that ended on its own - lease denied (another process
            // already holds this credential) or lease lost mid-poll - so a still-active credential is
            // eligible for a fresh acquire attempt on this same tick rather than being stuck looking
            // "already polled" in `_pollers` forever after its own `Task` already completed. Checked
            // before the stale/start sections below so a reaped id can be picked back up by the "start"
            // loop in the same pass.
            foreach (var endedId in _pollers.Where(kv => kv.Value.Loop.IsCompleted).Select(kv => kv.Key).ToList())
            {
                var (cancellation, loop) = _pollers[endedId];
                await CancelAndAwaitAsync(cancellation, loop);
                _pollers.Remove(endedId);
            }

            // Stop polling a credential that is no longer active (revoked since the last refresh).
            foreach (var staleId in _pollers.Keys.Where(id => !activeIds.Contains(id)).ToList())
            {
                var (cancellation, loop) = _pollers[staleId];
                await CancelAndAwaitAsync(cancellation, loop);
                _pollers.Remove(staleId);
            }

            // Start polling a credential that just became active - or that was just reaped above,
            // whether it never held the lease or lost it mid-poll.
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
        await using var lease = await pollerOwnership.TryAcquireAsync(credentialId, cancellationToken);
        if (lease is null)
        {
            // Another process already holds this credential's poll loop (adr/0089) - not an error, and
            // not logged as one: this is the expected steady state for every credential this process
            // does not happen to have won. The next RefreshPollersAsync tick retries.
            return;
        }

        long? marker = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // adr/0089's half-open-connection guard: confirms the lease is still backed by a live
                // session before spending a long-poll timeout window believing it is still exclusive.
                await lease.VerifyStillHeldAsync(cancellationToken);

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
            catch (ChannelPollerLeaseLostException ex)
            {
                // adr/0089's half-open-connection window, surfaced. Not a transport failure - stop this
                // loop so RefreshPollersAsync's reap step retries the acquire fresh on its next tick,
                // rather than looping forever on a lease that can never become valid again.
                logger.LogWarning(
                    ex, "Lost poll ownership of MAX credential {ChannelCredentialId}; another process may take it over on its next refresh tick.",
                    credentialId.Value);
                return;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                // `14-16`: after this change a 409 here should not happen - this process holds the lease,
                // so no *other* Worker process should be calling MAX's own updates endpoint for this same
                // token. Logged distinctly from the generic transport-failure line below specifically so a
                // self-inflicted conflict (a bug in this mechanism) and a genuine provider-side conflict
                // are never confused with an ordinary transient failure.
                logger.LogWarning(
                    ex, "MAX returned 409 Conflict for credential {ChannelCredentialId} while this process holds its poll lease - " +
                    "unexpected after adr/0089; a provider-side conflict, not a topology one.",
                    credentialId.Value);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(pollingOptions.Value.ErrorBackoffSeconds), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
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
                new ExternalChannelAddress(parsed.ChatId.ToString()),
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
