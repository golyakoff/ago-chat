using System.Security.Cryptography;
using System.Text;

namespace Ago.Chat.Domain;

/// <summary>
/// `14-02`: the credential a shop hands AGO so AGO can act as that shop's own bot on one external
/// channel - a MAX bot token today, an SMS long-number's own API key or a Telegram bot token once
/// `14-03`/`14-05` ship. Channel-neutral by construction, not "MaxBotCredential" - `adr/0069` names the
/// reason this type carries no provider vocabulary at all: <see cref="ChannelPortTests"/>'s own arch
/// rule (`14-01`) refuses any type above Infrastructure whose name starts with a provider's own name,
/// and a credential concept that will need the identical shape for `14-03`'s SMS aggregator key is not
/// a MAX-only fact to begin with. <see cref="ChannelIdentity"/> is this type's nearest sibling in the
/// codebase and was the template: one row per (site, channel), not a value object on <see cref="Site"/>,
/// for the matching reason - a credential's own lifecycle (registered, revoked) is independent of
/// everything else a site holds.
///
/// <para><b>One bot per tenant per channel, decided 2026-08-27 (this item's own backlog note,
/// `adr/0069`).</b> The unique index on (site_id, kind) in
/// <c>ChannelCredentialConfiguration</c> is the storage-level backstop; this type's own
/// <see cref="Register"/> is called only after <c>RegisterChannelCredentialHandler</c> has confirmed no
/// active credential already exists for that pair, the same "index is the backstop, not the primary
/// mechanism" division `adr/0019` draws for <c>messages</c>.</para>
///
/// <para><b>Two secrets, two different at-rest treatments, and that asymmetry is `adr/0069`'s central
/// point.</b> <see cref="TokenCiphertext"/> is reversible (AES-256-GCM, the identical primitive
/// <see cref="WebhookEndpoint.SecretCiphertext"/> already uses) because AGO must reproduce the exact
/// token on every future outbound call to the provider - a one-way hash cannot support that, the same
/// reasoning <see cref="WebhookEndpoint"/>'s own remarks give for its own ciphertext column.
/// <see cref="WebhookSecretHash"/> is one-way (SHA-256) because AGO generated that value itself, handed
/// it to the provider exactly once at registration, and only ever needs to <em>verify</em> a candidate
/// against it afterward - the same "verified, not reproduced" shape a login password hash has, which
/// <see cref="ExternalMessageId.ToClientMessageId"/> already established is fine to compute with a bare
/// <c>SHA256</c> call directly in Domain (pure, deterministic, no I/O - not a port-worthy resource under
/// CLAUDE.md rule 2).</para>
///
/// <para><b>The console never reads <see cref="TokenCiphertext"/> back in decrypted form</b> - unlike
/// <see cref="WebhookEndpoint.SecretCiphertext"/>, whose plaintext is legitimately shown to the tenant
/// once at registration (that secret is AGO's own value, generated for the tenant's benefit). A MAX bot
/// token is the shop's own secret, entered once and never AGO's to redisplay - `adr/0069`'s "the console
/// never shows it back" decision. No Application handler this item ships ever calls
/// <c>IChannelCredentialCipher.Decrypt</c> for a read path; the only caller is the outbound send inside
/// <c>Ago.Chat.Infrastructure.MaxBot</c>.</para>
/// </summary>
public sealed class ChannelCredential
{
    public ChannelCredentialId Id { get; }

    public SiteId SiteId { get; }

    public ChannelKind Kind { get; }

    /// <summary>AES-256-GCM ciphertext over the provider token - opaque bytes to Domain, the same
    /// "Domain never sees the key or the algorithm's own parameters" shape
    /// <see cref="WebhookEndpoint.SecretCiphertext"/> uses.</summary>
    public byte[] TokenCiphertext { get; } = [];

    /// <summary>SHA-256 of the webhook secret AGO generated and handed to the provider at registration
    /// - see this type's own remarks for why this is a hash and not a ciphertext.</summary>
    public byte[] WebhookSecretHash { get; } = [];

    public bool Active { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// `14-08`: the identifier, at the provider's own side, that pins the token to one specific
    /// addressable account there - <see langword="null"/> for MAX and Telegram, whose bot tokens are
    /// self-addressing (every call already carries the token, and the token alone tells the provider
    /// which bot to act as). VK is the first channel where the token is not enough on its own: VK's
    /// <c>messages.send</c> takes a separate <c>group_id</c> alongside a group access token
    /// (confirmed against VK's own official SDK - see <c>Ago.Chat.Infrastructure.Vk.VkChannelAdapter</c>'s
    /// own remarks), so that class's outbound call needs somewhere to keep it. Named generically rather
    /// than <c>VkGroupId</c> for the identical reason
    /// <see cref="TokenCiphertext"/> and <see cref="WebhookSecretHash"/> are not <c>MaxBotToken</c>: a
    /// future channel with the same "token plus a second, less-secret identifier" shape (this item's own
    /// report names none yet) reuses this column instead of adding its own.
    /// </summary>
    public string? ProviderAccountId { get; }

    private ChannelCredential(
        ChannelCredentialId id, SiteId siteId, ChannelKind kind, byte[] tokenCiphertext,
        byte[] webhookSecretHash, bool active, DateTimeOffset createdAt, string? providerAccountId)
    {
        Id = id;
        SiteId = siteId;
        Kind = kind;
        TokenCiphertext = tokenCiphertext;
        WebhookSecretHash = webhookSecretHash;
        Active = active;
        CreatedAt = createdAt;
        ProviderAccountId = providerAccountId;
    }

    // EF Core materialization only (1-04's precedent) - never called by domain code.
    private ChannelCredential()
    {
    }

    /// <summary>
    /// Binds one shop-supplied token to one site's channel, for the first time. The caller (Application)
    /// has already checked that no active credential exists for this (site, kind) pair - see this type's
    /// own remarks on the unique-index backstop.
    ///
    /// <para><paramref name="providerAccountId"/> defaults to <see langword="null"/> so MAX's and
    /// Telegram's own call sites, both written before this parameter existed, need no change - this
    /// item's own "additive, not a breaking change to a shared shape" discipline
    /// (`db-migration`'s own "additive-first" rule, applied here to a constructor rather than a
    /// column).</para>
    /// </summary>
    public static ChannelCredential Register(
        ChannelCredentialId id, SiteId siteId, ChannelKind kind, byte[] tokenCiphertext,
        byte[] webhookSecretHash, DateTimeOffset now, string? providerAccountId = null) =>
        new(id, siteId, kind, tokenCiphertext, webhookSecretHash, active: true, now, providerAccountId);

    /// <summary>
    /// Constant-time comparison of a candidate webhook secret (as received on an inbound MAX request's
    /// <c>X-Max-Bot-Api-Secret</c> header) against <see cref="WebhookSecretHash"/>. A plain
    /// <c>SequenceEqual</c> here would leak the true hash one byte at a time to a timing attacker the
    /// same way <c>Ago.Chat.FakeCrm.WebhookSignatureVerifier.Verify</c>'s own remarks already explain for
    /// the mirror (outbound) case.
    /// </summary>
    public bool MatchesWebhookSecret(string candidateSecret)
    {
        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidateSecret));
        return CryptographicOperations.FixedTimeEquals(candidateHash, WebhookSecretHash);
    }

    /// <summary>
    /// Terminal, never a hard delete - <see cref="WebhookEndpoint.Revoke"/>'s own precedent, for the
    /// same reason: a revoked credential's existence (and its <see cref="CreatedAt"/>) stays queryable
    /// for support/audit, and re-activating an old, already-superseded token would defeat
    /// "revoke-and-recreate only". This is also `16-02`'s erasure hook and tenant-offboarding's own:
    /// deleting the row entirely (rather than flipping this flag) is a separate, explicit operation left
    /// to whichever of those items needs it - `adr/0069`'s own scope stops at "revocable", not "erasable
    /// on this path".
    /// </summary>
    public void Revoke()
    {
        if (!Active)
        {
            throw new InvalidChannelCredentialStateException($"Channel credential {Id.Value} is already revoked.");
        }

        Active = false;
    }
}
