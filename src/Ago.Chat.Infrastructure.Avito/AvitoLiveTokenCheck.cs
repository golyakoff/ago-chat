using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Avito;

/// <summary>
/// `25-177`: bounds a live <c>GET /core/v1/accounts/self</c> check with a timeout, and classifies the
/// result into exactly three outcomes a caller can render distinctly -
/// <see cref="AvitoLiveCheckOutcome.Verified"/>, <see cref="AvitoLiveCheckOutcome.ProviderUnreachable"/>
/// and a refusal (<see cref="AvitoLiveCheckOutcome.RefusalReason"/> set, both flags false) - the identical
/// timeout/three-outcome scaffolding <c>Ago.Chat.Infrastructure.Telegram.TelegramLiveTokenCheck</c>/
/// <c>Ago.Chat.Infrastructure.MaxBot.MaxLiveTokenCheck</c>/<c>Ago.Chat.Infrastructure.Vk.VkLiveTokenCheck</c>/
/// <c>Ago.Chat.Infrastructure.WhatsApp.WhatsAppLiveTokenCheck</c> already establish - see any of those
/// types' own remarks for the fuller reasoning behind the bound itself, not repeated here.
///
/// <para><b>Why this file's own shape is not a fifth repeat of the same three-argument
/// <c>RunAsync(client, token, timeout, ct)</c> signature.</b> Every sibling channel holds one durable
/// token: a live check reads it, calls the provider once, and is done - any write-back (MAX's/WhatsApp's
/// own <c>PublicHandle</c> backfill) happens in the calling endpoint, after this method returns. Avito's
/// own access token <em>expires</em> (`docs/backlog/25-177-*.md`'s own "What is actually true today"), and
/// the Decision that item's own author made, 2026-09-20 ("refresh eagerly"), requires this method itself
/// to refresh and persist a new pair before it can answer at all - so unlike every sibling, this method
/// takes the full <see cref="ChannelCredential"/> plus <see cref="IChannelCredentialRepository"/>/
/// <see cref="IChannelCredentialCipher"/> directly, rather than a bare decrypted token string.</para>
///
/// <para><b>`GetSelfAsync` never throws <see cref="AvitoAccessTokenExpiredException"/> - a confirmed gap
/// between the backlog item's own stated premise and the real code, found while building this file.</b>
/// That exception exists only on <see cref="AvitoApiClient.SendMessageAsync"/>'s own 401 branch;
/// <see cref="AvitoApiClient.GetSelfAsync"/> throws the generic <see cref="AvitoApiCallException"/> for
/// every non-success status, 401 included (<c>AvitoApiClientTests.GetSelfAsync_WhenAvitoRejectsTheToken_ThrowsAvitoApiCallException</c>
/// proves this is the real, tested contract, not an oversight to "fix" here). Widening
/// <see cref="AvitoApiClient.GetSelfAsync"/> itself to throw the specific type on 401 was considered and
/// rejected: <c>AvitoChannelEndpoints.HandleConnectAsync</c> catches <see cref="AvitoApiCallException"/>
/// around this same call today, and a freshly pasted, never-yet-valid token that happens to answer 401 must
/// keep refusing the connect attempt cleanly - widening <see cref="AvitoApiClient.GetSelfAsync"/>'s own
/// exception contract would silently break that unrelated, already-tested path for a distinction this
/// method does not actually need (below). So this method reacts to the one exception
/// <see cref="AvitoApiClient.GetSelfAsync"/> really throws, and treats <em>any</em> such failure as
/// "attempt a refresh, if a refresh token is stored" - not only a confirmed-401 case - which still produces
/// the exact three Done-when outcomes the backlog item asks for: a genuinely revoked grant fails the
/// refresh too (<see cref="AvitoApiClient.RefreshAccessTokenAsync"/> needs the identical grant to still be
/// valid), so it still ends in <see cref="AvitoLiveCheckOutcome.Refused"/>; an expired-but-refreshable
/// token's refresh succeeds, so it still ends in <see cref="AvitoLiveCheckOutcome.Verified"/>.</para>
///
/// <para><b>The eager refresh, and the one live call after it that Telegram's/MAX's/VK's/WhatsApp's own
/// checks never need.</b> A successful <see cref="AvitoApiClient.RefreshAccessTokenAsync"/> call is
/// persisted immediately (<see cref="ChannelCredential.RotateOAuthTokens"/>) - the identical "persist
/// before the retry" discipline <see cref="AvitoChannelAdapter.RefreshAndPersistAsync"/> already proves for
/// the send path, so a crash between refresh and persist cannot strand the credential on a refresh token
/// Avito has already invalidated. The freshly refreshed access token is then verified with one more
/// <c>GetSelfAsync</c> call, mirroring <see cref="AvitoChannelAdapter.SendAsync"/>'s own bounded
/// "refreshed once, retried once, never a second refresh" shape - a second failure here is a refusal, not
/// another refresh attempt.</para>
///
/// <para><b>The concurrency risk the Decision names, and how this method settles it without any signal
/// from Avito's own API.</b> Avito's refresh tokens are one-shot and rotate on use, so two concurrent
/// status reads that both find the same expired access token can both attempt
/// <see cref="AvitoApiClient.RefreshAccessTokenAsync"/> with the identical, now-single-use, refresh token -
/// the loser's own call to Avito fails. <b>Avito's own error shape gives this method no way to tell that
/// failure apart from a genuinely dead grant</b> - confirmed against the real, tested contract:
/// <c>AvitoApiClientTests.RefreshAccessTokenAsync_WhenAvitoRejectsTheRefreshToken_ThrowsAvitoApiCallException</c>
/// shows Avito's own <c>/token</c> failures share the identical generic
/// <c>{"error":{"code":N,"message":"..."}}</c> envelope every other endpoint in this class uses, with no
/// documented, distinct code for "this refresh token was already used" versus "this refresh token is
/// simply invalid" - there is no OAuth-standard <c>invalid_grant</c> string anywhere in this provider's own
/// schema for this method's catch clause to key on. So this method disambiguates locally instead of relying
/// on the provider at all: on a refresh failure, it reloads this credential's own row
/// (<see cref="IChannelCredentialRepository.ReloadAsync"/>) and checks whether the refresh token actually
/// stored there right now is still the one this call just tried. A changed value proves somebody else's
/// concurrent read already won the race and refreshed this credential - the Decision's own instruction to
/// treat that as "reload and use whatever is there now", not an error, applies, and this method reports
/// <see cref="AvitoLiveCheckOutcome.Verified"/> without attempting a second live call: the winner's own
/// request already exercised this exact code path (including its own post-refresh verification call above)
/// to reach that state. An unchanged value after every retry below means nobody else touched it - the
/// refresh genuinely failed, so this reports <see cref="AvitoLiveCheckOutcome.Refused"/>.</para>
///
/// <para><b>Why the reload is retried a bounded few times, a detail the backlog item's own "reload and use
/// whatever is there now" wording does not spell out.</b> The race has two independent legs with no
/// ordering guarantee between them: Avito itself invalidates the loser's refresh token the instant the
/// winner's own call to <c>/token</c> succeeds, but the winner's own database write
/// (<see cref="IChannelCredentialRepository.SaveAsync"/>) is a separate, subsequent round trip that could
/// still be in flight at that exact moment. A loser that reloaded immediately after its own failed refresh
/// could observe the still-old row and wrongly conclude "genuinely revoked" before the winner's write ever
/// lands. <see cref="ReloadRetryDelay"/>/<see cref="MaxReloadAttempts"/> bound this the same way every
/// other wait in this stage is bounded (this codebase's own "err toward retrying briefly over a false
/// failure" instinct, at a scale - milliseconds, not seconds - appropriate to a local database round trip
/// rather than a third-party HTTP call).</para>
/// </summary>
public static class AvitoLiveTokenCheck
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ReloadRetryDelay = TimeSpan.FromMilliseconds(50);

    private const int MaxReloadAttempts = 5;

    public static async Task<AvitoLiveCheckOutcome> RunAsync(
        AvitoApiClient client,
        IChannelCredentialRepository credentials,
        IChannelCredentialCipher cipher,
        AvitoApiOptions options,
        ChannelCredential credential,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var accessToken = cipher.Decrypt(credential.TokenCiphertext);

        try
        {
            await client.GetSelfAsync(accessToken, linkedCts.Token);
            return AvitoLiveCheckOutcome.Verified();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // linkedCts fired because timeoutCts did, not because the caller's own request was aborted -
            // TelegramLiveTokenCheck's own remarks on the identical branch.
            return AvitoLiveCheckOutcome.ProviderUnreachable();
        }
        catch (HttpRequestException)
        {
            return AvitoLiveCheckOutcome.ProviderUnreachable();
        }
        catch (AvitoApiCallException ex)
        {
            // This file's own remarks above: GetSelfAsync throws this for every non-success status, 401
            // included, so this is the branch that reacts to an expired-and-refreshable token as well as
            // a genuinely bad one - the refresh attempt below is what actually tells the two apart.
            return await RefreshAndRecoverAsync(
                client, credentials, cipher, options, credential, ex.Message, linkedCts, cancellationToken);
        }
    }

    private static async Task<AvitoLiveCheckOutcome> RefreshAndRecoverAsync(
        AvitoApiClient client, IChannelCredentialRepository credentials, IChannelCredentialCipher cipher,
        AvitoApiOptions options, ChannelCredential credential, string originalFailureReason,
        CancellationTokenSource linkedCts, CancellationToken cancellationToken)
    {
        if (credential.RefreshTokenCiphertext is not { } refreshCiphertext)
        {
            // AvitoChannelAdapter.SendAsync's own "no refresh token stored" branch - should not happen for
            // any credential this system's own connect endpoint created (it always supplies one), but a
            // row from before Avito shipped a refresh token at all is not this method's problem to solve.
            return AvitoLiveCheckOutcome.Refused(originalFailureReason);
        }

        var attemptedRefreshToken = cipher.Decrypt(refreshCiphertext);

        AvitoRefreshTokenResponse refreshed;
        try
        {
            refreshed = await client.RefreshAccessTokenAsync(
                options.ClientId, options.ClientSecret, attemptedRefreshToken, linkedCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AvitoLiveCheckOutcome.ProviderUnreachable();
        }
        catch (HttpRequestException)
        {
            return AvitoLiveCheckOutcome.ProviderUnreachable();
        }
        catch (AvitoApiCallException)
        {
            // Could be a genuinely dead grant, or someone else's concurrent read already won the race and
            // rotated this exact refresh token out from under this call - this file's own remarks on why
            // Avito's response alone cannot tell the two apart, and why the answer is a local reload
            // instead.
            return await RecoverFromPossibleRefreshRaceAsync(
                credentials, cipher, credential, attemptedRefreshToken, cancellationToken);
        }

        // Persist before the verification retry, mirroring AvitoChannelAdapter.RefreshAndPersistAsync's
        // own "never strand the credential on an already-invalidated refresh token" ordering.
        credential.RotateOAuthTokens(
            cipher.Encrypt(refreshed.AccessToken!), cipher.Encrypt(refreshed.RefreshToken!));
        await credentials.SaveAsync(credential, cancellationToken);

        try
        {
            // Never surface the transient "was expired a moment ago" fact to the caller (the Decision's
            // own wording) - report evidence that the *new* token actually works, not merely that Avito
            // accepted the refresh request.
            await client.GetSelfAsync(refreshed.AccessToken!, linkedCts.Token);
            return AvitoLiveCheckOutcome.Verified();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AvitoLiveCheckOutcome.ProviderUnreachable();
        }
        catch (HttpRequestException)
        {
            return AvitoLiveCheckOutcome.ProviderUnreachable();
        }
        catch (AvitoApiCallException ex)
        {
            // Refreshed once, immediately rejected again - AvitoChannelAdapter.SendAsync's own bounded
            // "not retried a second time" discipline for the identical shape.
            return AvitoLiveCheckOutcome.Refused(ex.Message);
        }
    }

    private static async Task<AvitoLiveCheckOutcome> RecoverFromPossibleRefreshRaceAsync(
        IChannelCredentialRepository credentials, IChannelCredentialCipher cipher, ChannelCredential credential,
        string attemptedRefreshToken, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxReloadAttempts; attempt++)
        {
            await credentials.ReloadAsync(credential, cancellationToken);

            if (credential.RefreshTokenCiphertext is not { } currentRefreshCiphertext)
            {
                // Revoked out from under this call entirely (an operator disconnected mid-check) - not
                // the "someone else refreshed it" case this method exists for.
                break;
            }

            if (cipher.Decrypt(currentRefreshCiphertext) != attemptedRefreshToken)
            {
                // The stored refresh token has moved on since this call read it - somebody else's
                // concurrent status read already refreshed and persisted a new pair, and (per this file's
                // own remarks) already verified that new access token itself. Use whatever is there now,
                // per the Decision, rather than spending a second live call re-proving what the winner's
                // own request already proved.
                return AvitoLiveCheckOutcome.Verified();
            }

            if (attempt < MaxReloadAttempts - 1)
            {
                await Task.Delay(ReloadRetryDelay, cancellationToken);
            }
        }

        return AvitoLiveCheckOutcome.Refused(
            "Avito refused to refresh this channel's access token, and no other concurrent request "
            + "appears to have refreshed it either - reconnect this channel.");
    }
}

/// <summary>Exactly three shapes, never a fourth - the identical discipline
/// <c>Ago.Chat.Infrastructure.Vk.VkLiveCheckOutcome</c> already establishes for a channel with nothing to
/// backfill (Avito gets no <see cref="ChannelCredential.PublicHandle"/>, ever, per `25-147` - unlike
/// MAX's/WhatsApp's/Telegram's own outcome types, this one carries no handle field at all, and this
/// file's own <see cref="AvitoLiveTokenCheck"/> never calls <see cref="ChannelCredential.SetPublicHandle"/>
/// anywhere).</summary>
public sealed record AvitoLiveCheckOutcome(bool Ok, bool Unreachable, string? RefusalReason)
{
    public static AvitoLiveCheckOutcome Verified() => new(true, false, null);

    public static AvitoLiveCheckOutcome Refused(string reason) => new(false, false, reason);

    public static AvitoLiveCheckOutcome ProviderUnreachable() => new(false, true, null);
}
