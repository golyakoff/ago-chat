using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ReceiveChannelMessage;
using Ago.Chat.Api.Http;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.MaxBot;

namespace Ago.Chat.Api.Channels;

/// <summary>
/// `14-02`: MAX's own production inbound mechanism - a webhook receiver, placed in <c>Ago.Chat.Api</c>
/// rather than <c>Ago.Chat.Webhooks</c>. That deserves the explanation this item's own backlog note asks
/// for: `adr/0013` made <c>Ago.Chat.Webhooks</c> a bulkhead for <em>outbound</em> calls to a shop's own
/// CRM - "expected to be slow and failing; must not affect the others." An inbound MAX webhook has the
/// opposite failure profile: it is a request <em>we</em> must answer quickly (MAX's own 30-second
/// response window, ten retries on anything else), doing bounded local work (a handful of Postgres
/// writes through <see cref="ReceiveChannelMessageHandler"/>, the same pipeline a widget message already
/// uses) - request-shaped, not third-party-latency-shaped. That is exactly the "a webhook receiver is
/// request-shaped (Api), a poller is restart-tolerant background work (Worker)" split this item's own
/// Scope section states, and it is why this endpoint sits beside every other inbound HTTP route in this
/// host rather than opening a fourth reason to isolate a process.
///
/// <para><b>Authentication, confirmed against MAX's own subscription mechanism.</b> MAX's
/// <c>POST /subscriptions</c> call accepts a <c>secret</c> alongside the callback <c>url</c>, and echoes
/// it back on every webhook delivery as the <c>X-Max-Bot-Api-Secret</c> header - the mirror of `6-03`'s
/// outbound <c>X-Ago-Signature</c> scheme, for the inbound direction this item's backlog note asks about.
/// <see cref="Domain.ChannelCredential.MatchesWebhookSecret"/> is the constant-time check against the
/// value this system generated at registration and never showed to anyone (`adr/0069`). The
/// <c>{credentialId}</c> path segment is <em>routing</em>, not authentication on its own - it says which
/// tenant's secret to check against, not that the caller is trusted; a request whose secret does not
/// match this specific credential is rejected regardless of how the id was obtained.</para>
/// </summary>
public static class MaxWebhookEndpoints
{
    public const string SecretHeaderName = "X-Max-Bot-Api-Secret";

    public static void MapMaxWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/webhooks/max/{credentialId:guid}", HandleAsync);
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
            // A missing id and a revoked one read identically - the same info-hiding shape
            // WebhookEndpoints/DeleteAttachmentHandler already use elsewhere in this codebase, so a
            // revoked credential's own URL can never be used to fingerprint which tenants exist or ever
            // connected MAX.
            return Results.NotFound();
        }

        var secretHeader = httpContext.Request.Headers[SecretHeaderName].ToString();
        if (string.IsNullOrEmpty(secretHeader) || !credential.MatchesWebhookSecret(secretHeader))
        {
            return Results.Unauthorized();
        }

        MaxUpdate? update;
        try
        {
            update = await JsonSerializer.DeserializeAsync<MaxUpdate>(httpContext.Request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }

        // MAX retries a non-2xx delivery up to ten times (this item's backlog note) - an update this
        // deserialized fine but this item has no use case for (a malformed body, or an update_type
        // other than message_created) is acknowledged with 200 rather than rejected, so MAX does not
        // burn its retry budget resending something that will never parse differently.
        var parsed = update is null ? null : MaxInboundMessageParser.TryParse(update);
        if (parsed is null)
        {
            return Results.Ok();
        }

        var result = await receiveHandler.HandleAsync(
            new ReceiveChannelMessage(
                credential.SiteId,
                ChannelKind.Max,
                new ExternalChannelAddress(parsed.SenderId.ToString()),
                new ExternalMessageId(parsed.ExternalMessageId),
                parsed.Text),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok();
    }
}
