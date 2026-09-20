using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.GetChannelCredentialStatus;
using Ago.Chat.Application.UseCases.RegisterChannelCredential;
using Ago.Chat.Application.UseCases.RevokeChannelCredential;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.WhatsApp;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Api.Channels;

/// <summary>
/// `14-10`/`adr/0069`: the console's own WhatsApp connection flow - the same
/// <c>"RequireOperatorIdentity"</c> policy <see cref="MaxChannelEndpoints"/>/<see cref="TelegramChannelEndpoints"/>/
/// <see cref="VkChannelEndpoints"/> already use.
///
/// <para><b>Why the connect request carries both a token and a <c>phoneNumberId</c>, unlike every
/// precedent.</b> MAX's/Telegram's bot tokens are self-addressing; VK's <c>groups.getById</c> discovers
/// the community id from the token alone, with no <c>group_id</c> parameter. WhatsApp offers neither: a
/// WhatsApp Business Account can hold more than one phone number, and Meta's own Graph API has no "which
/// number does this token mean by default" call - so the operator supplies the number's own
/// <c>phone_number_id</c> directly (read off Meta's own App Dashboard, or the Embedded Signup response),
/// and <see cref="WhatsAppApiClient.GetPhoneNumberAsync"/>'s own call validates that the token is actually
/// authorized for that specific number, rather than discovering it independently the way VK's own call
/// does. <see cref="WhatsAppApiClient.GetPhoneNumberAsync"/>'s own remarks have the full contrast.</para>
///
/// <para><b>Validated before ever writing a <see cref="ChannelCredential"/> row - the identical ordering
/// <see cref="VkChannelEndpoints"/> already established, for the identical reason.</b> WhatsApp's own
/// phone-number lookup needs nothing this system generates, so a bad token or a phone number id the
/// token cannot access is refused with nothing ever written to storage.</para>
///
/// <para><b>The response carries neither a callback URL nor a webhook secret, unlike
/// <see cref="VkChannelEndpoints.ConnectVkChannelResponse"/> and like
/// <see cref="MaxChannelEndpoints.ConnectMaxChannelResponse"/>/<see cref="TelegramChannelEndpoints.ConnectTelegramChannelResponse"/>.</b>
/// WhatsApp's inbound webhook is App-wide, configured once against AGO's own Meta App
/// (<see cref="WhatsAppBotApiOptions"/>' own remarks) - there is no per-credential URL or secret for a
/// human to paste anywhere, so this endpoint has nothing of that shape to hand back.</para>
///
/// <para><b>Connecting is refused outright while this deployment has not configured
/// <see cref="WhatsAppBotApiOptions.AppSecret"/>/<see cref="WhatsAppBotApiOptions.VerifyToken"/> -
/// the identical reasoning <see cref="VkChannelEndpoints"/>' own <c>PublicWebhookBaseUrl</c> check
/// applies.</b> Without either, no inbound WhatsApp delivery to this deployment could ever be
/// authenticated and accepted, so accepting a token now would silently promise a channel that can send
/// but never receive.</para>
///
/// <para><b>`25-176`: <see cref="HandleStatusAsync"/> now has MAX-style live-verification parity,
/// correcting `25-65`'s own note above.</b> That note read WhatsApp as having no cheap, side-effect-free
/// per-request check the way Telegram's `getMe` is - re-reading it found <c>GetPhoneNumberAsync</c> itself
/// is exactly that: a plain <c>GET</c>, safe to repeat on every read, the identical "no side effect"
/// character <see cref="WhatsAppApiClient.GetPhoneNumberAsync"/>'s own remarks already draw to
/// <c>VkApiClient.GetGroupInfoAsync</c>. So this endpoint now calls
/// <see cref="WhatsAppLiveTokenCheck.RunAsync"/> on every read, the same bounded, three-outcome shape
/// <see cref="MaxChannelEndpoints.HandleStatusAsync"/>/<see cref="VkChannelEndpoints.HandleStatusAsync"/>
/// already use - see <see cref="WhatsAppLiveTokenCheck"/>'s own remarks for why its shape is a genuine
/// combination of both siblings' own precedents (VK's exception-catching control flow, MAX's
/// carries-a-handle outcome), not a repeat of either alone.</para>
///
/// <para><b>Unlike VK, this endpoint's live check also backfills <see cref="Domain.ChannelCredential.PublicHandle"/>
/// on a status read - the identical `25-147` pattern <see cref="MaxChannelEndpoints.HandleStatusAsync"/>
/// already implements.</b> WhatsApp already has a real public handle to keep current
/// (`display_phone_number`), unlike VK's fully-derived deep link - see
/// <see cref="WhatsAppLiveCheckOutcome"/>'s own remarks.</para>
/// </summary>
public static class WhatsAppChannelEndpoints
{
    public static void MapWhatsAppChannelEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sites/{siteId:guid}/channels/whatsapp")
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
        WhatsAppApiClient whatsAppApiClient,
        IClock clock,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var site = new SiteId(siteId);

        var status = await statusHandler.HandleAsync(
            new GetChannelCredentialStatus(user.GetOperatorId(), site, ChannelKind.WhatsApp), cancellationToken);
        if (status.IsFailure)
        {
            return status.Error!.Value.ToProblem(httpContext);
        }

        if (status.Value.ChannelCredentialId is not { } credentialId)
        {
            return Results.Ok(WhatsAppChannelStatusResponse.NotConnected(clock.UtcNow));
        }

        // `GetChannelCredentialStatusHandler` already confirmed this operator may manage this site's
        // channels and that this id belongs to it - this second repository call exists only to reach
        // TokenCiphertext/ProviderAccountId, which that channel-neutral handler's own result deliberately
        // never carries (the identical reason `MaxChannelEndpoints.HandleStatusAsync`/
        // `VkChannelEndpoints.HandleStatusAsync` make this same second call). A credential revoked
        // between the two calls (an operator double-clicking Disconnect in another tab) is not an error -
        // it means "no longer connected", answered the same way as if it had never existed.
        var credential = await credentials.GetByIdAsync(credentialId, cancellationToken);
        if (credential is null || !credential.Active)
        {
            return Results.Ok(WhatsAppChannelStatusResponse.NotConnected(clock.UtcNow));
        }

        var token = cipher.Decrypt(credential.TokenCiphertext);
        var outcome = await WhatsAppLiveTokenCheck.RunAsync(
            whatsAppApiClient, token, credential.ProviderAccountId!, WhatsAppLiveTokenCheck.Timeout, cancellationToken);

        // `25-176`: backfill on this same live read, no reconnect required - the identical `25-147`
        // pattern `MaxChannelEndpoints.HandleStatusAsync` already implements. Never gates or changes
        // anything about `outcome` itself: a verified-but-handle-less number still reports Verified: true
        // here exactly as it did before this item, this is purely an additional, silent write alongside
        // the existing read.
        if (outcome.Ok && outcome.DisplayPhoneNumber is { Length: > 0 } number && credential.PublicHandle != number)
        {
            credential.SetPublicHandle(number);
            await credentials.SaveAsync(credential, cancellationToken);
        }

        return Results.Ok(new WhatsAppChannelStatusResponse(
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
        ConnectWhatsAppChannelRequest request,
        RegisterChannelCredentialHandler registerHandler,
        WhatsAppApiClient whatsAppApiClient,
        IOptions<WhatsAppBotApiOptions> whatsAppOptions,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var options = whatsAppOptions.Value;
        if (options.AppSecret is not { Length: > 0 } || options.VerifyToken is not { Length: > 0 })
        {
            return ConversationErrors.ChannelNotAvailable(
                "WhatsApp is not available on this deployment - no App-level webhook secret/verify token is configured.")
                .ToProblem(httpContext);
        }

        if (string.IsNullOrWhiteSpace(request.PhoneNumberId))
        {
            return ConversationErrors.ChannelInvalidToken("A WhatsApp phone number id is required.").ToProblem(httpContext);
        }

        var user = httpContext.User;
        var site = new SiteId(siteId);

        WhatsAppPhoneNumberInfo phoneNumberInfo;
        try
        {
            phoneNumberInfo = await whatsAppApiClient.GetPhoneNumberAsync(request.Token, request.PhoneNumberId, cancellationToken);
        }
        catch (WhatsAppApiCallException ex)
        {
            return ConversationErrors.ChannelInvalidToken(ex.Message).ToProblem(httpContext);
        }

        // `25-147`: `display_phone_number` was already fetched by GetPhoneNumberAsync above and, before
        // this item, simply discarded - phoneNumberInfo.Id (Meta's own phone_number_id, an
        // inbound-routing key) is never a fact to expose, but the display number is exactly the public
        // fact a deep link (`wa.me/<number>`) needs. Passed straight into Register, unlike Telegram: this
        // endpoint's own call to GetPhoneNumberAsync already ran before Register, matching VK's own
        // "validate, then register" ordering.
        var registered = await registerHandler.HandleAsync(
            new RegisterChannelCredential(
                user.GetOperatorId(), site, ChannelKind.WhatsApp, request.Token, phoneNumberInfo.Id,
                PublicHandle: phoneNumberInfo.DisplayPhoneNumber),
            cancellationToken);
        if (registered.IsFailure)
        {
            return registered.Error!.Value.ToProblem(httpContext);
        }

        return Results.Created(
            $"/api/v1/sites/{siteId}/channels/whatsapp/{registered.Value.ChannelCredentialId.Value}",
            new ConnectWhatsAppChannelResponse(registered.Value.ChannelCredentialId.Value, registered.Value.CreatedAt));
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

    public sealed record ConnectWhatsAppChannelRequest(string Token, string PhoneNumberId);

    public sealed record ConnectWhatsAppChannelResponse(Guid ChannelCredentialId, DateTimeOffset CreatedAt);

    /// <summary>
    /// `25-176`: the same seven fields <see cref="MaxChannelEndpoints.MaxChannelStatusResponse"/>/
    /// <see cref="VkChannelEndpoints.VkChannelStatusResponse"/> already carry, repeated here rather than
    /// extracted into a shared shape - this codebase's own established convention is two independent
    /// per-channel response records (`25-09`'s own remarks on
    /// <see cref="MaxChannelEndpoints.MaxChannelStatusResponse"/> give the fuller reasoning; `25-65`'s own
    /// three-field version of this type used to be the odd one out precisely because
    /// <see cref="HandleStatusAsync"/> never asked WhatsApp anything - now that it does, this type
    /// converges on the same fields by coincidence of behaviour, not because a base type was introduced).
    /// No field is shaped like a secret, the same guarantee <see cref="ConnectWhatsAppChannelResponse"/>
    /// already makes.
    /// </summary>
    public sealed record WhatsAppChannelStatusResponse(
        bool Connected,
        Guid? ChannelCredentialId,
        DateTimeOffset? CreatedAt,
        bool? Verified,
        bool Unreachable,
        string? RefusalReason,
        DateTimeOffset CheckedAt)
    {
        public static WhatsAppChannelStatusResponse NotConnected(DateTimeOffset checkedAt) =>
            new(Connected: false, ChannelCredentialId: null, CreatedAt: null, Verified: null, Unreachable: false,
                RefusalReason: null, CheckedAt: checkedAt);
    }
}
