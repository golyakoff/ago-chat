using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ReceiveChannelMessage;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.WhatsApp;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Api.Channels;

/// <summary>
/// `14-10`: WhatsApp's own production inbound mechanism, and its <em>only</em> one - the identical
/// "webhook only" shape `14-08` established for VK, though for a different reason (VK's own API offers a
/// separate, unbuilt long-poll alternative; Meta's Cloud API offers none at all). Placed in
/// <c>Ago.Chat.Api</c> rather than <c>Ago.Chat.Webhooks</c>, the identical reasoning
/// <c>MaxWebhookEndpoints</c>'/<c>VkWebhookEndpoints</c>' own remarks give: `adr/0013` made
/// <c>Ago.Chat.Webhooks</c> a bulkhead for <em>outbound</em> calls to a shop's own CRM, and an inbound
/// WhatsApp delivery has the opposite failure profile - a request <em>we</em> must answer quickly, doing
/// bounded local work, request-shaped rather than third-party-latency-shaped.
///
/// <para><b>One route, not one per tenant - the central shape difference from every precedent.</b> MAX's/
/// Telegram's/VK's own webhook paths each carry a <c>{credentialId}</c> segment because each provider's
/// own webhook mechanism is configured per bot/community. Meta's own "tech provider" model puts every
/// onboarded client behind one App-wide callback URL (<see cref="WhatsAppBotApiOptions"/>' own remarks,
/// citing Meta's own Embedded Signup documentation), so this endpoint has no path segment to route on at
/// all - tenant attribution happens after authentication, from the payload's own <c>phone_number_id</c>
/// (<see cref="IChannelCredentialRepository.GetActiveByProviderAccountIdAsync"/>), not before it the way
/// every other channel's <c>{credentialId}</c> segment lets it happen.</para>
///
/// <para><b>The GET verification handshake - confirmed from Meta's own Graph API webhooks documentation,
/// 2026-08-30.</b> Meta sends a one-time (and, whenever an admin re-triggers verification from the App
/// Dashboard, repeatable) <c>GET</c> request carrying three query parameters: <c>hub.mode=subscribe</c>,
/// <c>hub.verify_token</c> (the value AGO itself chose and pasted into the App Dashboard -
/// <see cref="WhatsAppBotApiOptions.VerifyToken"/>) and <c>hub.challenge</c> (an arbitrary value to echo
/// back). A matching token gets the raw <c>hub.challenge</c> value back as the entire plain-text response
/// body; anything else gets refused. Shaped differently from VK's own confirmation handshake (a static
/// preshared value compared on a <c>GET</c>, versus VK's live <c>groups.getCallbackConfirmationCode</c>
/// API call) but serving the identical purpose - proving this endpoint's own operator, not just its URL,
/// owns the callback before Meta starts delivering real traffic to it.</para>
///
/// <para><b>Authentication for every POST delivery - confirmed from Meta's own Graph API webhooks
/// documentation.</b> Meta signs the raw request body with HMAC-SHA256, keyed by
/// <see cref="WhatsAppBotApiOptions.AppSecret"/> (AGO's own Meta App secret, not a tenant's), and sends
/// the result as <c>X-Hub-Signature-256: sha256={hex digest}</c> - Meta's own generic Graph API webhook
/// signing mechanism, not specific to WhatsApp. This is the mirror of `6-03`'s outbound
/// <c>X-Ago-Signature</c> scheme and of <see cref="ChannelCredential.MatchesWebhookSecret"/>'s own
/// per-credential check, but computed against one App-wide key rather than a value
/// <c>RegisterChannelCredentialHandler</c> generates per credential - there is no per-credential secret
/// for this channel to check at all (<see cref="WhatsAppBotApiOptions"/>' own remarks). The raw body bytes
/// must be signed exactly as delivered, before any JSON parsing, so this handler buffers the body once and
/// signs/deserializes from the identical bytes rather than re-serializing a parsed object (which could
/// legitimately produce different bytes and always fail the check).</para>
/// </summary>
public static class WhatsAppWebhookEndpoints
{
    public const string SignatureHeaderName = "X-Hub-Signature-256";
    private const string SignaturePrefix = "sha256=";

    public static void MapWhatsAppWebhookEndpoints(this WebApplication app)
    {
        app.MapGet("/webhooks/whatsapp", HandleVerificationAsync);
        app.MapPost("/webhooks/whatsapp", HandleDeliveryAsync);
    }

    private static IResult HandleVerificationAsync(HttpContext httpContext, IOptions<WhatsAppBotApiOptions> options)
    {
        var configuredToken = options.Value.VerifyToken;
        if (configuredToken is not { Length: > 0 })
        {
            // WhatsAppChannelEndpoints' own remarks: with no VerifyToken configured, this deployment has
            // not opted into WhatsApp at all - a clear "not enabled here" refusal rather than pretending
            // to own a callback URL nobody configured.
            return Results.NotFound();
        }

        var query = httpContext.Request.Query;
        var mode = query["hub.mode"].ToString();
        var token = query["hub.verify_token"].ToString();
        var challenge = query["hub.challenge"].ToString();

        if (mode != "subscribe" || token != configuredToken || challenge is not { Length: > 0 })
        {
            // Results.StatusCode, not Results.Forbid() - the latter is shaped for an authentication
            // handler's own challenge/forbid flow (Ago.Chat.Api.Auth's cookie/bearer schemes elsewhere in
            // this host) and throws with no scheme configured; this refusal has nothing to do with this
            // system's own operator authentication, so it answers with a bare status code instead.
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        return Results.Text(challenge);
    }

    private static async Task<IResult> HandleDeliveryAsync(
        HttpContext httpContext,
        IOptions<WhatsAppBotApiOptions> options,
        IChannelCredentialRepository credentials,
        ReceiveChannelMessageHandler receiveHandler,
        CancellationToken cancellationToken)
    {
        var appSecret = options.Value.AppSecret;
        if (appSecret is not { Length: > 0 })
        {
            // Fail closed, not open - WhatsAppBotApiOptions' own remarks: with no AppSecret configured
            // there is nothing to verify a delivery's signature against, so every delivery is untrusted
            // by construction.
            return Results.Unauthorized();
        }

        using var bodyReader = new StreamReader(httpContext.Request.Body, Encoding.UTF8);
        var rawBody = await bodyReader.ReadToEndAsync(cancellationToken);
        var bodyBytes = Encoding.UTF8.GetBytes(rawBody);

        var signatureHeader = httpContext.Request.Headers[SignatureHeaderName].ToString();
        if (!IsValidSignature(bodyBytes, appSecret, signatureHeader))
        {
            return Results.Unauthorized();
        }

        WhatsAppWebhookEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<WhatsAppWebhookEnvelope>(bodyBytes);
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }

        if (envelope is null)
        {
            return Results.BadRequest();
        }

        var parsedMessages = WhatsAppInboundMessageParser.Parse(envelope);
        foreach (var parsed in parsedMessages)
        {
            var credential = await credentials.GetActiveByProviderAccountIdAsync(
                ChannelKind.WhatsApp, parsed.PhoneNumberId, cancellationToken);
            if (credential is null)
            {
                // No tenant on this deployment currently owns this phone_number_id - never registered,
                // or since revoked. Meta retries a non-2xx delivery, so this is acknowledged rather than
                // rejected: nothing about retrying would let this system attribute the message to
                // anyone, the identical "this item has no use case for it, acknowledge and move on"
                // reasoning VkWebhookEndpoints' own remarks give for an unrecognised event.
                continue;
            }

            var result = await receiveHandler.HandleAsync(
                new ReceiveChannelMessage(
                    credential.SiteId,
                    ChannelKind.WhatsApp,
                    new ExternalChannelAddress(parsed.From),
                    new ExternalMessageId(parsed.ExternalMessageId),
                    parsed.Text),
                cancellationToken);

            if (result.IsFailure)
            {
                // A genuine processing failure (not "unrecognised message") - Meta's own retry-on-non-2xx
                // behaviour is the correct response here, and ReceiveChannelMessageHandler's own
                // idempotency (ExternalMessageId.ToClientMessageId) makes a retried redelivery of a
                // multi-message batch safe even for the messages already processed successfully above.
                return result.Error!.Value.ToProblem(httpContext);
            }
        }

        return Results.Ok();
    }

    private static bool IsValidSignature(byte[] bodyBytes, string appSecret, string signatureHeader)
    {
        if (!signatureHeader.StartsWith(SignaturePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), bodyBytes);
        var providedHex = signatureHeader[SignaturePrefix.Length..];

        byte[] provided;
        try
        {
            provided = Convert.FromHexString(providedHex);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}
