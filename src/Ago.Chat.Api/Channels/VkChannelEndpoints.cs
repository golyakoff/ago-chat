using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.GetChannelCredentialStatus;
using Ago.Chat.Application.UseCases.RegisterChannelCredential;
using Ago.Chat.Application.UseCases.RevokeChannelCredential;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Vk;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Api.Channels;

/// <summary>
/// `14-08`/`adr/0069`: the console's own VK connection flow - the same
/// <c>"RequireOperatorIdentity"</c> policy <see cref="MaxChannelEndpoints"/>/<see cref="TelegramChannelEndpoints"/>
/// already use.
///
/// <para><b>`25-65`'s own note on why <see cref="HandleStatusAsync"/> did not call VK, corrected by
/// `25-175`.</b> `25-65` read <c>groups.getById</c> (used only at connect time before this item, above)
/// as unsafe to repeat on every console page load - re-reading that note found the call itself is a
/// plain read, the same "no side effect" character <see cref="VkApiClient.GetGroupInfoAsync"/>'s own
/// remarks draw to Telegram's own <c>getMe</c>; the real reason was scope and cost (every screen view
/// making a live VK call on the tenant's behalf), the identical tradeoff Telegram already accepts for
/// itself. `25-175`'s own backlog file records the author's explicit decision to accept it for VK too.
/// So this endpoint now calls <see cref="VkLiveTokenCheck.RunAsync"/> on every read, the same bounded,
/// three-outcome shape <see cref="MaxChannelEndpoints.HandleStatusAsync"/>/
/// <see cref="TelegramChannelEndpoints.HandleStatusAsync"/> already use - see <see cref="VkLiveTokenCheck"/>'s
/// own remarks for the one place VK's own client genuinely differs (it throws rather than returning a
/// result object).</para>
///
/// <para><b>`25-175`: no <see cref="Domain.ChannelCredential.PublicHandle"/> write here, unlike either
/// precedent.</b> VK has nothing to backfill - see <see cref="VkLiveCheckOutcome"/>'s own remarks and
/// <see cref="HandleConnectAsync"/>'s own comment below on why <c>ProviderAccountId</c> alone is always
/// enough. This endpoint's live check is pure status-richness (<c>Verified</c>/<c>Unreachable</c>/
/// <c>RefusalReason</c>), never a second write path.</para>
///
/// <para><b>Why this endpoint validates the token, and discovers VK's own community id, <em>before</em>
/// ever writing a <see cref="ChannelCredential"/> row - unlike both precedents, which must create the
/// row first and roll it back on a rejection.</b> MAX's own subscribe call needs the webhook secret this
/// system generates, which does not exist before <see cref="RegisterChannelCredentialHandler"/> creates
/// the row; Telegram's <c>getMe</c> check does not strictly need that ordering but follows it anyway for
/// consistency. VK's <c>groups.getById</c> needs nothing but the raw token the operator just typed in -
/// no dependency on anything this system generates - so this endpoint calls it first. A bad or
/// already-revoked token is refused with nothing ever written to storage, which is simpler than either
/// precedent's create-then-roll-back dance, not a shortcut: it is only possible because of what VK's own
/// API happens to need, not a pattern this item is proposing MAX or Telegram should have used
/// instead.</para>
///
/// <para><b>Why the response carries the webhook secret and the callback URL in plaintext, unlike
/// <see cref="MaxChannelEndpoints.ConnectMaxChannelResponse"/>/<see cref="TelegramChannelEndpoints.ConnectTelegramChannelResponse"/>,
/// which carry neither.</b> MAX registers its own webhook programmatically
/// (<c>POST /subscriptions</c>); Telegram has no webhook at all. VK's Callback API is configured by a
/// human, in VK's own community settings UI, pasting in a URL and a secret key - so the human has to be
/// given both. <c>RegisteredChannelCredential.WebhookSecret</c>'s own remarks record why this is not a
/// new exception to `adr/0069`'s "console never shows it back": that rule is about the shop's own token,
/// never about a secret AGO generated for the shop's benefit, and this is the first channel where a
/// human - not an API call - is the one who needs it.</para>
/// </summary>
public static class VkChannelEndpoints
{
    public static void MapVkChannelEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sites/{siteId:guid}/channels/vk")
            .RequireAuthorization("RequireOperatorIdentity");

        group.MapGet("", HandleStatusAsync);
        group.MapPost("", HandleConnectAsync);
        group.MapDelete("/{channelCredentialId:guid}", HandleDisconnectAsync);
    }

    private static async Task<IResult> HandleStatusAsync(
        Guid siteId,
        GetChannelCredentialStatusHandler statusHandler,
        IChannelCredentialRepository credentials,
        IChannelCredentialCipher cipher,
        VkApiClient vkApiClient,
        IClock clock,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var site = new SiteId(siteId);

        var status = await statusHandler.HandleAsync(
            new GetChannelCredentialStatus(user.GetOperatorId(), site, ChannelKind.Vk), cancellationToken);
        if (status.IsFailure)
        {
            return status.Error!.Value.ToProblem(httpContext);
        }

        if (status.Value.ChannelCredentialId is not { } credentialId)
        {
            return Results.Ok(VkChannelStatusResponse.NotConnected(clock.UtcNow));
        }

        // `GetChannelCredentialStatusHandler` already confirmed this operator may manage this site's
        // channels and that this id belongs to it - this second repository call exists only to reach
        // TokenCiphertext, which that channel-neutral handler's own result deliberately never carries
        // (the identical reason `TelegramChannelEndpoints.HandleStatusAsync`/`MaxChannelEndpoints.HandleStatusAsync`
        // make this same second call). A credential revoked between the two calls (an operator
        // double-clicking Disconnect in another tab) is not an error - it means "no longer connected",
        // answered the same way as if it had never existed.
        var credential = await credentials.GetByIdAsync(credentialId, cancellationToken);
        if (credential is null || !credential.Active)
        {
            return Results.Ok(VkChannelStatusResponse.NotConnected(clock.UtcNow));
        }

        var token = cipher.Decrypt(credential.TokenCiphertext);
        var outcome = await VkLiveTokenCheck.RunAsync(vkApiClient, token, VkLiveTokenCheck.Timeout, cancellationToken);

        // `25-175`: deliberately no `credential.SetPublicHandle(...)` here - see this class's own
        // remarks and `VkLiveCheckOutcome`'s own remarks for why VK has nothing to backfill.
        return Results.Ok(new VkChannelStatusResponse(
            Connected: true,
            ChannelCredentialId: credentialId.Value,
            CreatedAt: status.Value.CreatedAt,
            Verified: outcome.Unreachable ? null : outcome.Ok,
            Unreachable: outcome.Unreachable,
            RefusalReason: outcome.RefusalReason,
            CheckedAt: clock.UtcNow));
    }

    private static async Task<IResult> HandleConnectAsync(
        Guid siteId,
        ConnectVkChannelRequest request,
        RegisterChannelCredentialHandler registerHandler,
        VkApiClient vkApiClient,
        IOptions<VkBotApiOptions> vkOptions,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (vkOptions.Value.PublicWebhookBaseUrl is not { } publicBase)
        {
            // VkBotApiOptions' own remarks: unlike MAX, there is no fallback inbound mechanism to skip
            // to - without a public URL there is nothing this system could ever hand VK to call back to,
            // so connecting is refused outright rather than silently accepting a token nothing will ever
            // deliver to.
            return ConversationErrors.ChannelNotAvailable(
                "VK is not available on this deployment - no public webhook base URL is configured.").ToProblem(httpContext);
        }

        var user = httpContext.User;
        var site = new SiteId(siteId);

        VkGroupInfo groupInfo;
        try
        {
            groupInfo = await vkApiClient.GetGroupInfoAsync(request.Token, cancellationToken);
        }
        catch (VkApiCallException ex)
        {
            return ConversationErrors.ChannelInvalidToken(ex.Message).ToProblem(httpContext);
        }

        // `25-147`: no PublicHandle is ever passed here, deliberately - unlike WhatsApp, VK's own public
        // deep link (`vk.me/club<id>`) is fully derivable from ProviderAccountId alone, with nothing else
        // that could ever need to change independently of it. Storing a second, redundant column would
        // only invite the two to drift; `Ago.Chat.Infrastructure.Postgres.PublicChannelLinkReadStore`
        // computes the link at read time instead (`docs/backlog/25-147-*.md`'s own decision).
        var registered = await registerHandler.HandleAsync(
            new RegisterChannelCredential(
                user.GetOperatorId(), site, ChannelKind.Vk, request.Token, groupInfo.GroupId.ToString()),
            cancellationToken);
        if (registered.IsFailure)
        {
            return registered.Error!.Value.ToProblem(httpContext);
        }

        var credentialId = registered.Value.ChannelCredentialId;
        var callbackUrl = new Uri(publicBase, $"webhooks/vk/{credentialId.Value}");

        return Results.Created(
            $"/api/v1/sites/{siteId}/channels/vk/{credentialId.Value}",
            new ConnectVkChannelResponse(credentialId.Value, registered.Value.CreatedAt, callbackUrl.ToString(), registered.Value.WebhookSecret));
    }

    private static async Task<IResult> HandleDisconnectAsync(
        Guid siteId,
        Guid channelCredentialId,
        RevokeChannelCredentialHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new RevokeChannelCredential(new ChannelCredentialId(channelCredentialId), user.GetOperatorId(), new SiteId(siteId)),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.NoContent();
    }

    public sealed record ConnectVkChannelRequest(string Token);

    /// <summary><see cref="CallbackUrl"/>/<see cref="WebhookSecret"/> are what an operator pastes into
    /// VK's own community Callback API settings page - see this class's own remarks for why this
    /// response, unlike MAX's/Telegram's, carries them at all.</summary>
    public sealed record ConnectVkChannelResponse(
        Guid ChannelCredentialId, DateTimeOffset CreatedAt, string CallbackUrl, string WebhookSecret);

    /// <summary>
    /// `25-175`: the same seven fields <see cref="MaxChannelEndpoints.MaxChannelStatusResponse"/>/
    /// <see cref="TelegramChannelEndpoints.TelegramChannelStatusResponse"/> already carry, repeated here
    /// rather than extracted into a shared shape - this codebase's own established convention is two
    /// independent per-channel response records (`25-09`'s own remarks on
    /// <see cref="MaxChannelEndpoints.MaxChannelStatusResponse"/> give the fuller reasoning). No field is
    /// shaped like a secret, the same guarantee <see cref="ConnectVkChannelResponse"/> already makes for
    /// the connect response.
    ///
    /// <para><b>Unlike either sibling, no field here ever backfills anything.</b> See
    /// <see cref="HandleStatusAsync"/>'s own remarks and <see cref="VkLiveCheckOutcome"/>'s own remarks
    /// for why VK has no <c>PublicHandle</c>-shaped gap to close.</para>
    /// </summary>
    public sealed record VkChannelStatusResponse(
        bool Connected,
        Guid? ChannelCredentialId,
        DateTimeOffset? CreatedAt,
        bool? Verified,
        bool Unreachable,
        string? RefusalReason,
        DateTimeOffset CheckedAt)
    {
        public static VkChannelStatusResponse NotConnected(DateTimeOffset checkedAt) =>
            new(Connected: false, ChannelCredentialId: null, CreatedAt: null, Verified: null, Unreachable: false,
                RefusalReason: null, CheckedAt: checkedAt);
    }
}
