using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ReceiveChannelMessage;
using Ago.Chat.Application.UseCases.RecordChannelVisitorContact;
using Ago.Chat.Api.Http;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.MaxBot;
using Microsoft.Extensions.Logging;

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
        IChannelCredentialCipher cipher,
        ReceiveChannelMessageHandler receiveHandler,
        RecordChannelVisitorContactHandler recordContactHandler,
        ILogger<RecordChannelVisitorContactHandler> logger,
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

        // `25-151`: decrypted unconditionally, alongside the ordinary text path below, rather than only
        // when a contact attachment is actually present - MaxInboundMessageParser's own trust check is
        // the only thing that needs it, but this endpoint has no cheap way to know an update carries a
        // contact before parsing it, and MaxLongPollingService's own iteration-level decrypt already
        // accepts the identical cost per update.
        var token = cipher.Decrypt(credential.TokenCiphertext);

        // MAX retries a non-2xx delivery up to ten times (this item's backlog note) - an update this
        // deserialized fine but this item has no use case for (a malformed body, or an update_type
        // other than message_created) is acknowledged with 200 rather than rejected, so MAX does not
        // burn its retry budget resending something that will never parse differently.
        var parsed = update is null ? null : MaxInboundMessageParser.TryParse(update, token);
        if (parsed is null)
        {
            // `25-151`: a contact attachment present but rejected by MaxInboundMessageParser's own hash
            // check reads identically to "nothing this parser understood" to TryParse's own caller -
            // re-inspecting the raw update here is the only way to log the rejection distinctly from an
            // ordinary skipped update, the same split MaxLongPollingService's own dispatch makes for the
            // identical case on its own inbound mechanism.
            if (update?.Message?.Body?.Attachments?.Any(a => a.Type == "contact") == true)
            {
                logger.LogWarning(
                    "Rejected a shared MAX contact for site {SiteId}: the attachment's hash did not verify.",
                    credential.SiteId.Value);
            }

            return Results.Ok();
        }

        var problem = default(IResult);

        if (!string.IsNullOrWhiteSpace(parsed.Text))
        {
            var result = await receiveHandler.HandleAsync(
                new ReceiveChannelMessage(
                    credential.SiteId,
                    ChannelKind.Max,
                    new ExternalChannelAddress(parsed.ChatId.ToString()),
                    new ExternalMessageId(parsed.ExternalMessageId),
                    parsed.Text),
                cancellationToken);

            if (result.IsFailure)
            {
                problem = result.Error!.Value.ToProblem(httpContext);
            }
        }

        // `25-151`: a verified contact share, dispatched to its own sibling command rather than folded
        // into ReceiveChannelMessage above - see RecordChannelVisitorContact's own remarks for why. Its
        // own failure (e.g. `24-05`'s consent gate) is logged, never turned into a non-2xx response - a
        // rejected/unrecordable contact is not the kind of transient failure MAX's own retry budget
        // exists for, unlike the ordinary-message failure above, which keeps its existing ToProblem
        // behaviour unchanged.
        if (parsed.Contact is { } contact)
        {
            var contactResult = await recordContactHandler.HandleAsync(
                new RecordChannelVisitorContact(
                    credential.SiteId,
                    ChannelKind.Max,
                    new ExternalChannelAddress(parsed.ChatId.ToString()),
                    contact.Phone,
                    MaxContactName.Compose(contact.FirstName, contact.LastName)),
                cancellationToken);

            if (contactResult.IsFailure)
            {
                logger.LogWarning(
                    "Could not record a shared MAX contact for site {SiteId}: {Code} {Message}",
                    credential.SiteId.Value, contactResult.Error!.Value.Code, contactResult.Error!.Value.Message);
            }
        }

        return problem ?? Results.Ok();
    }
}
