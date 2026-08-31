using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ReceiveChannelMessage;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Email;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Api.Channels;

/// <summary>
/// `14-09`: email's own production inbound mechanism, and its <em>only</em> one - the identical "webhook
/// only" shape `14-08`/`14-10` each established for VK/WhatsApp, for a related but distinct reason: this
/// item's own honesty note (<see cref="EmailInboundWebhookPayload"/>'s own remarks) explains that there is
/// no real inbound-parse provider in play at all, only this item's own invented contract for a future,
/// currently-unbuilt Postfix pipe-transport script (ago-deploy's own work, out of this item's scope).
/// Placed in <c>Ago.Chat.Api</c> rather than <c>Ago.Chat.Webhooks</c>, the identical reasoning
/// <c>MaxWebhookEndpoints</c>'/<c>VkWebhookEndpoints</c>'/<c>WhatsAppWebhookEndpoints</c>' own remarks give:
/// `adr/0013` made <c>Ago.Chat.Webhooks</c> a bulkhead for <em>outbound</em> calls to a shop's own CRM, and
/// an inbound email delivery has the opposite failure profile - a request this system must answer quickly,
/// doing bounded local work.
///
/// <para><b>One route, no per-tenant path segment - the same shape WhatsApp's own App-wide webhook has, for
/// a different underlying reason.</b> WhatsApp has one route because Meta's own "tech provider" model puts
/// every onboarded client behind one callback URL. Email has one route because there is no per-tenant
/// account to configure a callback <em>for</em> at all (<see cref="EmailBotApiOptions"/>'s own remarks) -
/// every site's own mail funnels through the identical subaddress scheme to the identical pickup mechanism,
/// so tenant attribution happens from the payload's own recipient address
/// (<see cref="EmailInboundMessageParser"/>/<see cref="EmailRecipientAddress"/>'s own remarks), not from a
/// URL.</para>
///
/// <para><b>Authentication - the App-wide shared-secret shape <see cref="EmailBotApiOptions"/>'s own
/// remarks explain, computed the identical way WhatsApp's own <c>X-Hub-Signature-256</c> is (HMAC-SHA256
/// over the raw request body) but under this system's own header name, <c>X-Ago-Email-Signature</c> -
/// `messaging.md`'s own <c>X-Ago-Signature</c> naming convention for a signature this system defines
/// itself, rather than a vendor's own header name, since (per this file's own class-level remarks) there is
/// no vendor here to match. The raw body bytes must be signed exactly as delivered, before any JSON
/// parsing, so this handler buffers the body once and signs/deserializes from the identical bytes -
/// <see cref="WhatsAppWebhookEndpoints"/>'s own remarks explain why re-serializing a parsed object would be
/// wrong.</b></para>
///
/// <para><b>An unrecognised or malformed delivery is acknowledged (<c>200 OK</c>) rather than rejected,
/// once past authentication - the identical "acknowledge what cannot be attributed to a real tenant, rather
/// than reject it" treatment <c>WhatsAppWebhookEndpoints</c>' own remarks give an unrecognised
/// <c>phone_number_id</c>.</b> A malformed JSON body is the one case that still gets a real error status
/// (<c>400</c>) - that is this system's own trusted pickup script sending something broken, not a stranger's
/// traffic, and the identical distinction <see cref="WhatsAppWebhookEndpoints"/>'s own JSON-deserialize
/// failure already draws.</para>
/// </summary>
public static class EmailWebhookEndpoints
{
    public const string SignatureHeaderName = "X-Ago-Email-Signature";
    private const string SignaturePrefix = "sha256=";

    public static void MapEmailWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/webhooks/email", HandleDeliveryAsync);
    }

    private static async Task<IResult> HandleDeliveryAsync(
        HttpContext httpContext,
        IOptions<EmailBotApiOptions> options,
        ISiteRepository sites,
        IEmailThreadStore threads,
        ReceiveChannelMessageHandler receiveHandler,
        CancellationToken cancellationToken)
    {
        var emailOptions = options.Value;
        if (emailOptions.WebhookSecret is not { Length: > 0 })
        {
            // Fail closed, not open - EmailBotApiOptions' own remarks: with no WebhookSecret configured
            // there is nothing to verify a delivery's signature against, so every delivery is untrusted
            // by construction.
            return Results.Unauthorized();
        }

        using var bodyReader = new StreamReader(httpContext.Request.Body, Encoding.UTF8);
        var rawBody = await bodyReader.ReadToEndAsync(cancellationToken);
        var bodyBytes = Encoding.UTF8.GetBytes(rawBody);

        var signatureHeader = httpContext.Request.Headers[SignatureHeaderName].ToString();
        if (!IsValidSignature(bodyBytes, emailOptions.WebhookSecret, signatureHeader))
        {
            return Results.Unauthorized();
        }

        EmailInboundWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<EmailInboundWebhookPayload>(bodyBytes);
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }

        if (payload is null)
        {
            return Results.BadRequest();
        }

        var parsed = EmailInboundMessageParser.Parse(payload, emailOptions);
        if (parsed is null)
        {
            // Either the payload's own required fields were missing (this system's own trusted script
            // sent something incomplete), or the recipient address does not resolve to any site this
            // deployment could attribute the message to - either way, nothing further can be done with
            // it, and this class's own remarks explain why that is acknowledged rather than rejected.
            return Results.Ok();
        }

        var site = await sites.GetByIdAsync(parsed.SiteId, cancellationToken);
        if (site is null)
        {
            // A well-formed subaddress naming a SiteId this deployment does not (or no longer) have -
            // EmailRecipientAddress's own remarks explain why a parseable id is not proof a site exists.
            return Results.Ok();
        }

        var result = await receiveHandler.HandleAsync(
            new ReceiveChannelMessage(
                parsed.SiteId, ChannelKind.Email, new ExternalChannelAddress(parsed.From),
                new ExternalMessageId(parsed.ExternalMessageId), parsed.Text),
            cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        // EmailThreadState's own remarks: this second, Email-specific write happens here, after the
        // shared pipeline has resolved a real ConversationId, rather than inside ReceiveChannelMessageHandler
        // itself - the channel-neutral command has no slot for a raw provider Message-ID and must not gain
        // one.
        var conversationId = result.Value.ConversationId;
        var existingThread = await threads.GetAsync(conversationId, cancellationToken);
        var thread = existingThread is null
            ? EmailThreadState.Start(conversationId, parsed.ExternalMessageId, parsed.Subject)
            : existingThread;

        if (existingThread is not null)
        {
            thread.RecordInbound(parsed.ExternalMessageId);
        }

        await threads.SaveAsync(thread, cancellationToken);

        return Results.Ok();
    }

    private static bool IsValidSignature(byte[] bodyBytes, string webhookSecret, string signatureHeader)
    {
        if (!signatureHeader.StartsWith(SignaturePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(webhookSecret), bodyBytes);
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
