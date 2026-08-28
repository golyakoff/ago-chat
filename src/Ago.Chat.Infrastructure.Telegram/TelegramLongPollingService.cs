using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ReceiveChannelMessage;
using Ago.Chat.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Infrastructure.Telegram;

/// <summary>
/// `14-07`: this channel's <b>only</b> inbound mechanism, and its permanent production one - not a
/// dev-only loop paired with a webhook receiver the way <c>MaxLongPollingService</c> is. Telegram's own
/// documentation treats long polling as a fully supported production mechanism in its own right, so
/// unlike MAX there is no second class here for a webhook receiver to build, no
/// <c>PublicWebhookBaseUrl</c>-style toggle in <see cref="TelegramBotApiOptions"/>, and no subscribe
/// call anywhere in this item - see that options class's own remarks for the full reasoning, including
/// why <c>adr/0070</c>'s relay fix (outbound only) does not change this conclusion.
///
/// <para><b>One polling loop per active Telegram credential, started and stopped as credentials come and
/// go</b> - identical shape to <c>MaxLongPollingService</c>, for the identical reason: Telegram's own API
/// shape is one token, one <c>GET /getUpdates</c> stream per bot, so one lightweight <see cref="Task"/>
/// per bot is what that shape already is, rather than multiplexing several bots' own cursors through one
/// loop. <see cref="_pollers"/> is this class's only mutable shared state, guarded by
/// <see cref="_gate"/> because the outer refresh loop and a poller's own faulted-task cleanup can both
/// touch it.</para>
/// </summary>
public sealed class TelegramLongPollingService(
    TelegramApiClient client,
    IServiceScopeFactory scopeFactory,
    IOptions<TelegramBotApiOptions> apiOptions,
    IOptions<TelegramLongPollingServiceOptions> pollingOptions,
    ILogger<TelegramLongPollingService> logger) : BackgroundService
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
            active = await credentials.GetAllActiveAsync(ChannelKind.Telegram, stoppingToken);
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

    /// <summary>One bot's own long-poll loop. Decrypts the token once per iteration - the identical
    /// "never risk a stale token surviving past its own revocation" trade-off
    /// <c>MaxLongPollingService.PollOneCredentialAsync</c>'s own remarks describe.</summary>
    private async Task PollOneCredentialAsync(ChannelCredentialId credentialId, CancellationToken cancellationToken)
    {
        long? offset = null;

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

                var result = await client.GetUpdatesAsync(
                    token, offset, apiOptions.Value.LongPollTimeoutSeconds, cancellationToken);

                foreach (var update in result.Updates)
                {
                    // Advance the acknowledgement cursor past every update seen this round, whether or
                    // not this loop understood it - an update this parser skips (no message, e.g. an
                    // edited_message or a callback_query) must still be acknowledged, or Telegram would
                    // hand it back forever and this bot's own getUpdates stream would never advance.
                    offset = update.UpdateId + 1;
                    await DispatchIfMessageAsync(siteId, update, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Telegram long-poll for credential {ChannelCredentialId} failed; retrying.", credentialId.Value);
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

    private async Task DispatchIfMessageAsync(SiteId siteId, TelegramUpdate update, CancellationToken cancellationToken)
    {
        var parsed = TelegramInboundMessageParser.TryParse(update);
        if (parsed is null)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ReceiveChannelMessageHandler>();

        var result = await handler.HandleAsync(
            new ReceiveChannelMessage(
                siteId, ChannelKind.Telegram,
                new ExternalChannelAddress(parsed.ChatId.ToString()),
                new ExternalMessageId(parsed.ExternalMessageId),
                parsed.Text),
            cancellationToken);

        if (result.IsFailure)
        {
            logger.LogWarning(
                "Could not receive a Telegram message for site {SiteId}: {Code} {Message}",
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
