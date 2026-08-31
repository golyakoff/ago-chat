using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Email;

/// <summary>
/// `14-09`: the tenant-routing decision this item's own backlog note asks to be "decided and recorded
/// explicitly, the same discipline `14-02`'s own routing section holds itself to" - unlike MAX/Telegram/
/// VK/WhatsApp (one bot/community/number per tenant, so the tenant is whichever <see cref="ChannelCredential"/>
/// owns the token), email has no per-tenant account to resolve a tenant from. **Decided: subaddressing off
/// one shared local part**, not a dedicated per-tenant address or a per-tenant subdomain - a real recipient
/// address looks like <c>support+3fa85f64-5717-4562-b3fc-2c963f66afa6@ago-chat.example</c>, where the
/// <c>+</c>-delimited suffix is a <see cref="SiteId"/>'s own <c>Guid</c> in <c>"N"</c> (no-hyphen) format.
///
/// <para><b>Why subaddressing over the two alternatives this item's own backlog note names.</b> A
/// dedicated address per site (<c>site-name@ago-chat.example</c>) needs a real mailbox or alias
/// provisioned per tenant on the mail server - genuine ago-deploy work, on a schedule this item's own
/// engineering-only scope cannot drive (a new site must become mailable the instant it is created, not
/// whenever someone next edits Postfix's alias map). A per-tenant subdomain
/// (<c>support@{site}.ago-chat.example</c>) needs a real DNS zone delegation or wildcard MX per tenant -
/// heavier still, and `10-05`'s own self-hosted relay has exactly one MX record today. Subaddressing needs
/// neither: Postfix's own <c>recipient_delimiter = +</c> setting (a single, one-time, deployment-wide
/// configuration line - ago-deploy's own work, out of this item's scope, named honestly rather than
/// silently assumed already in place) routes every <c>support+*@ago-chat.example</c> address to the
/// identical mailbox/pickup regardless of what follows the <c>+</c>, and the suffix survives all the way to
/// whatever reads the message. A brand-new site is mailable the instant <see cref="SiteId"/> exists, with
/// no second provisioning step anywhere - the same "no per-tenant secret, nothing for an operator to enter"
/// property <see cref="EmailBotApiOptions"/>'s own remarks already state for why this channel ships no
/// console connect endpoint at all.</para>
///
/// <para><b>Why the raw <see cref="SiteId"/>, not a generated opaque token the way a webhook secret would
/// be.</b> A site's own id is not a secret - it is already visible to any operator with console access, and
/// exposing it in a mailing address grants nothing beyond what "this shop uses AGO Chat" already reveals
/// (the same reasoning `WebhookEndpoint`'s own public-key-is-not-a-secret precedent applies to
/// <see cref="ISiteRepository.GetByPublicKeyAsync"/>). Minting and storing a second, opaque per-site token
/// purely to avoid putting a <see cref="Guid"/> in an email address would have been exactly the kind of
/// unrequired new secret-management surface this item's own scope has no reason to add.</para>
///
/// <para><b>A parsed <see cref="SiteId"/> is not proof a site exists.</b> This type only extracts the
/// candidate id from the address's own shape; <c>EmailWebhookEndpoints</c> still resolves it against
/// <see cref="ISiteRepository.GetByIdAsync"/> before doing anything with it, the same "acknowledge and drop
/// what cannot be attributed to a real tenant" treatment <c>WhatsAppWebhookEndpoints</c>'s own remarks give
/// an unrecognised <c>phone_number_id</c> - an arbitrary, well-formed but nonexistent guid in the local part
/// must not be treated as evidence a site exists.</para>
/// </summary>
public static class EmailRecipientAddress
{
    /// <summary>Builds the address a visitor should be told to mail for one site - what a console "here is
    /// your support address" display would show, once this item's own out-of-scope console piece is built.
    /// </summary>
    public static string Build(EmailBotApiOptions options, SiteId siteId) =>
        $"{options.SupportLocalPart}+{siteId.Value:N}@{options.Domain}";

    /// <summary>Extracts the candidate <see cref="SiteId"/> from a raw <c>To</c>/recipient address, or
    /// <see langword="null"/> if the address does not match this deployment's own
    /// <c>{SupportLocalPart}+{siteId}@{Domain}</c> shape at all (a malformed delivery, a stray RFC 2142
    /// alias like <c>postmaster@</c>, or traffic for a domain this deployment does not own).</summary>
    public static SiteId? TryParseSiteId(EmailBotApiOptions options, string? rawAddress)
    {
        if (string.IsNullOrWhiteSpace(rawAddress) || options.Domain is not { Length: > 0 })
        {
            return null;
        }

        var address = ExtractAddressSpec(rawAddress.Trim());

        var atIndex = address.LastIndexOf('@');
        if (atIndex < 0)
        {
            return null;
        }

        var localPart = address[..atIndex];
        var domainPart = address[(atIndex + 1)..];
        if (!string.Equals(domainPart, options.Domain, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var plusIndex = localPart.IndexOf('+');
        if (plusIndex < 0)
        {
            return null;
        }

        var mailbox = localPart[..plusIndex];
        var suffix = localPart[(plusIndex + 1)..];
        if (!string.Equals(mailbox, options.SupportLocalPart, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Guid.TryParseExact(suffix, "N", out var siteId) ? new SiteId(siteId) : null;
    }

    /// <summary>A real <c>To</c> header is frequently <c>"Display Name" &lt;address@domain&gt;</c>, not a
    /// bare address (RFC 5322's <c>mailbox</c> production) - this system's own inbound pickup script (out
    /// of this item's scope, see <see cref="EmailInboundWebhookPayload"/>'s own honesty note) is expected to
    /// hand over an address already extracted from that shape, but this method still strips a trailing
    /// <c>&lt;...&gt;</c> defensively rather than trusting every future caller to have done so - the same
    /// "validate only what is true generically, do not trust every caller" caution
    /// <c>ChannelCredentialTokenValidator</c>'s own remarks describe for a token's shape.</summary>
    private static string ExtractAddressSpec(string rawAddress)
    {
        var closeAngle = rawAddress.EndsWith('>') ? rawAddress.Length - 1 : -1;
        if (closeAngle < 0)
        {
            return rawAddress;
        }

        var openAngle = rawAddress.LastIndexOf('<', closeAngle);
        return openAngle < 0 ? rawAddress : rawAddress[(openAngle + 1)..closeAngle];
    }
}
