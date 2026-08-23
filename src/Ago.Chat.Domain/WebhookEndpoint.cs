namespace Ago.Chat.Domain;

/// <summary>
/// `6-03`: a tenant-registered outbound webhook target (`adr/0013`'s dispatcher reads this,
/// `adr/00XX` this item ships defines the signature scheme it will sign deliveries with). Its own
/// aggregate root, not folded into <see cref="Site"/> - a site's webhook endpoints have their own
/// lifecycle (registered, revoked) and their own transaction boundary, the same "does this change
/// independently, in its own transaction" test `Attachment`'s own remarks apply to justify *its*
/// separation from <see cref="Conversation"/>.
///
/// <see cref="SecretCiphertext"/>, not a hash: unlike a password (only ever *verified* by the party
/// that stores it), the signing secret must be *reproduced* by this system on every future delivery to
/// compute an HMAC-SHA256 signature (`adr/00XX`) - a one-way hash cannot support that, so this stores
/// a reversible ciphertext instead. See that ADR's own reasoning for why the "hash it like a password"
/// framing does not fit this specific case.
/// </summary>
public sealed class WebhookEndpoint
{
    public WebhookEndpointId Id { get; }

    public SiteId SiteId { get; }

    public Uri Url { get; } = null!;

    /// <summary>Encrypted at rest (`Ago.Chat.Application.Abstractions.IWebhookSecretCipher`) - the
    /// plaintext secret is handed to the caller exactly once, at <see cref="Register"/> time, and
    /// never stored anywhere in that form. Opaque bytes to Domain; only the cipher's own
    /// implementation (Infrastructure) knows the nonce/tag layout inside.</summary>
    public byte[] SecretCiphertext { get; } = [];

    public bool Active { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    private WebhookEndpoint(
        WebhookEndpointId id, SiteId siteId, Uri url, byte[] secretCiphertext, bool active, DateTimeOffset createdAt)
    {
        Id = id;
        SiteId = siteId;
        Url = url;
        SecretCiphertext = secretCiphertext;
        Active = active;
        CreatedAt = createdAt;
    }

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private WebhookEndpoint()
    {
    }

    /// <summary>
    /// The URL's own legality (https-only, not a private/loopback/link-local target) is a business
    /// rule checked in Application before this is ever called (`RegisterWebhookEndpointHandler`,
    /// `WebhookUrlValidator`) - the same "handler validates, domain constructs" split
    /// `CreateAttachmentHandler` already uses for content-type/size, rather than duplicating that
    /// policy as a Domain invariant it would then have no way to unit test in isolation from the rest
    /// of the aggregate.
    /// </summary>
    public static WebhookEndpoint Register(
        WebhookEndpointId id, SiteId siteId, Uri url, byte[] secretCiphertext, DateTimeOffset now) =>
        new(id, siteId, url, secretCiphertext, active: true, now);

    /// <summary>
    /// Flips <see cref="Active"/> to <see langword="false"/> - never a hard delete, so delivery
    /// history for this endpoint stays queryable afterward (this item's own Done-when). Terminal:
    /// unlike an attachment there is no path back, since re-enabling an old endpoint under its old
    /// (already-shown-once) secret would defeat "revoke-and-recreate only" (this item's own scope).
    /// </summary>
    public void Revoke()
    {
        if (!Active)
        {
            throw new InvalidWebhookEndpointStateException($"Webhook endpoint {Id.Value} is already revoked.");
        }

        Active = false;
    }
}
