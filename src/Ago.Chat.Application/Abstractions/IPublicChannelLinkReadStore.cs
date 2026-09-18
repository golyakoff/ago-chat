using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `25-148`: "which of this site's connected channels can a stranger already reach, and by what handle" -
/// modelled directly on <see cref="IEnabledModuleReadStore"/>'s own shape (adr/0004's Dapper side, the
/// same "hot, per-visitor-session read, never through the aggregate" reasoning that store's own remarks
/// give).
///
/// <para><b>A read store, not a fifth method on <see cref="IChannelCredentialRepository"/> - deliberately
/// (`docs/adr/0175-*.md`).</b> That repository's own <see cref="IChannelCredentialRepository.GetActiveAsync"/>/
/// <see cref="IChannelCredentialRepository.GetByIdAsync"/> return the whole <see cref="ChannelCredential"/>
/// aggregate, <see cref="ChannelCredential.TokenCiphertext"/> included - the shop's own bot token, still
/// encrypted, but still a value nothing on the visitor-facing handshake path has any business loading. A
/// read store that projects only <c>kind</c> and a public handle column cannot leak a token, structurally,
/// because it never selects one - the same "cannot leak what it never loaded" guarantee
/// <see cref="IEnabledModuleReadStore"/>'s own read (never <c>ModuleCredential</c> beyond what one caller
/// needs) already gives module credentials.</para>
/// </summary>
public interface IPublicChannelLinkReadStore
{
    /// <summary>
    /// One row per connected, handle-bearing channel - a site with nothing connected, or connected but
    /// with no known public handle yet (a MAX/Telegram bot whose `getMe` capture has not happened, or has
    /// not happened yet - `25-147`'s own item), simply has no row for that channel, never a row with a
    /// null or empty <see cref="PublicChannelLink.Handle"/>. Avito never appears here at all - it has no
    /// row-producing handle of any kind (`25-147`'s own "store nothing, and record why").
    /// </summary>
    Task<IReadOnlyList<PublicChannelLink>> GetForSiteAsync(SiteId siteId, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="Handle"/> is the raw, provider-specific fact - a Telegram/MAX bot's own `@username`, a
/// WhatsApp number's `display_phone_number`, or (for VK) the community id computed from
/// <see cref="ChannelCredential.ProviderAccountId"/> at read time, never stored (`25-147`'s own decision;
/// see <c>Ago.Chat.Infrastructure.Postgres.PublicChannelLinkReadStore</c>'s own remarks for the exact
/// formula). Never a full URL - building one from this raw value is
/// <c>Ago.Chat.Application.ChannelLinkUrlBuilder</c>'s own job, kept a separate step so this type carries
/// exactly the fact the database holds and nothing this store had to invent.
/// </summary>
public sealed record PublicChannelLink(ChannelKind Kind, string Handle);
