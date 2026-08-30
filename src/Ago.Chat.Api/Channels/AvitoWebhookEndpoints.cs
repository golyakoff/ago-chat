using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ReceiveChannelMessage;
using Ago.Chat.Api.Http;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Avito;

namespace Ago.Chat.Api.Channels;

/// <summary>
/// `14-11`: Avito's own production inbound mechanism - a webhook receiver, placed in
/// <c>Ago.Chat.Api</c> rather than <c>Ago.Chat.Webhooks</c>, the identical reasoning
/// <c>MaxWebhookEndpoints</c>'/<c>VkWebhookEndpoints</c>' own remarks give: `adr/0013` made
/// <c>Ago.Chat.Webhooks</c> a bulkhead for <em>outbound</em> calls to a shop's own CRM, and an inbound
/// Avito delivery has the opposite failure profile - a request <em>we</em> must answer quickly (Avito's
/// own documented 2-second timeout), doing bounded local work, request-shaped rather than
/// third-party-latency-shaped.
///
/// <para><b>Authentication - the one place this item's own design could not simply reuse
/// MAX's/VK's own shape, and why.</b> MAX's <c>POST /subscriptions</c> and VK's own Callback API settings
/// page both let AGO register a secret the provider echoes back on every delivery, checked via
/// <see cref="ChannelCredential.MatchesWebhookSecret"/> against a header
/// (<c>MaxWebhookEndpoints</c>'/<c>VkWebhookEndpoints</c>' own remarks). Avito's own <c>POST
/// /messenger/v3/webhook</c>, per this item's own research, accepts only a <c>url</c> - no secret field
/// to register and have echoed back on delivery. Avito does document an
/// <c>x-avito-messenger-signature</c> header on real deliveries, but this item found no documented
/// algorithm for it anywhere reachable, and a public developer thread (qna.habr.com/q/1404944, fetched
/// 2026-08-30) shows Avito's own support unable to answer the question for over a month - so this item
/// does not build against it. <b>What this item builds instead:</b> the registered callback URL itself
/// carries a query-string secret AGO generates
/// (<c>https://&lt;public-base&gt;/webhooks/avito/{credentialId}?secret=...</c>, <c>AvitoChannelEndpoints</c>'
/// own remarks) - the one thing Avito is guaranteed to preserve verbatim on every delivery, since Avito
/// calls back to exactly the URL AGO registered. This reuses <see cref="ChannelCredential.MatchesWebhookSecret"/>
/// unchanged, just reading the value from the query string instead of a header. <b>Named honestly: this
/// is a materially weaker guarantee than MAX's/VK's own header-echo mechanisms</b> - a URL (including its
/// query string) is more exposed to logging middleware, proxies and browser history than a header is, and
/// this item accepts that trade-off only because Avito's own API left no stronger option. The
/// <c>{credentialId}</c> path segment remains routing, not authentication on its own, the identical
/// info-hiding shape every precedent in this stage already establishes.</para>
/// </summary>
public static class AvitoWebhookEndpoints
{
    public const string SecretQueryParamName = "secret";

    public static void MapAvitoWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/webhooks/avito/{credentialId:guid}", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        Guid credentialId,
        HttpContext httpContext,
        IChannelCredentialRepository credentials,
        ReceiveChannelMessageHandler receiveHandler,
        CancellationToken cancellationToken)
    {
        var credential = await credentials.GetByIdAsync(new ChannelCredentialId(credentialId), cancellationToken);
        if (credential is null || !credential.Active)
        {
            // A missing id and a revoked one read identically - MaxWebhookEndpoints'/VkWebhookEndpoints'
            // own info-hiding shape, so a revoked credential's own URL can never be used to fingerprint
            // which tenants exist or ever connected Avito.
            return Results.NotFound();
        }

        var secret = httpContext.Request.Query[SecretQueryParamName].ToString();
        if (string.IsNullOrEmpty(secret) || !credential.MatchesWebhookSecret(secret))
        {
            return Results.Unauthorized();
        }

        AvitoWebhookEnvelope? envelope;
        try
        {
            envelope = await JsonSerializer.DeserializeAsync<AvitoWebhookEnvelope>(httpContext.Request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }

        // A delivery this deserialized fine but this item has no use case for (any payload type other
        // than "message", a non-text message, the seller's own outgoing echo, an a2u system chat) is
        // acknowledged with 200 rather than rejected - MaxWebhookEndpoints'/VkWebhookEndpoints' own
        // remarks on not burning a provider's own retry budget on something that will never parse
        // differently.
        var parsed = envelope is null ? null : AvitoInboundMessageParser.Parse(envelope);
        if (parsed is null)
        {
            return Results.Ok();
        }

        var result = await receiveHandler.HandleAsync(
            new ReceiveChannelMessage(
                credential.SiteId,
                ChannelKind.Avito,
                new ExternalChannelAddress(parsed.ChatId),
                new ExternalMessageId(parsed.ExternalMessageId),
                parsed.Text),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok();
    }
}
