using Ago.Chat.Domain;

namespace Ago.Chat.Application;

/// <summary>
/// `25-148`/`docs/adr/0175-*.md`: turns one <see cref="Application.Abstractions.PublicChannelLink"/>'s
/// raw handle into the full, absolute <c>https</c> URL <c>AuthEndpoints.VisitorSessionResponse.ChannelLinks</c>
/// actually carries - the load-bearing shape decision this item's own ADR records: the widget is handed a
/// URL it can open directly, never a bare handle it would have to template into a provider-specific URL
/// itself. A future channel with a public deep link needs one new arm here and zero `ago-widget` changes;
/// the alternative (sending <c>{kind, handle}</c> raw) would put provider vocabulary (`t.me/`, `max.ru/@`)
/// in the widget and require a widget release per channel.
///
/// <para>Pure and I/O-free - no port, no test double needed to exercise it. <b>Deliberately not placed
/// beside its nearest sibling, <see cref="ChannelEntitlementOptionKeys"/>, which is Domain.</b> That
/// type's own remarks draw the exact line this decision turns on: its <c>"channel-" + kind</c> naming is
/// "a stable fact about this codebase's own vocabulary... not something a deployment could reasonably
/// want to override" - this codebase invented that string. A provider's own public deep-link format
/// (`t.me/`, `max.ru/@`, `vk.me/club`) is the opposite kind of fact: external, owned by Telegram/MAX/VK
/// themselves, and not something this codebase could change even if it wanted to - the same
/// "provider-owned, not domain-owned" distinction that keeps a provider's wire DTOs out of Domain
/// entirely. It stays out of Infrastructure too, though, because unlike a DTO it needs no HTTP client, no
/// serialization attribute, and no provider SDK - it is presentation-shaping logic for one specific,
/// Application-owned wire response (<c>AuthEndpoints.VisitorSessionResponse</c>), which is exactly where
/// <see cref="Application.Abstractions.IPublicChannelLinkReadStore"/> and
/// <see cref="UseCases.MintVisitorChannelLinkCode.MintVisitorChannelLinkCodeHandler"/> - its only two
/// collaborators - already live.</para>
/// </summary>
public static class ChannelLinkUrlBuilder
{
    /// <summary>
    /// <see langword="null"/> for a <paramref name="handle"/> that is empty or all whitespace (defensive -
    /// <see cref="Application.Abstractions.IPublicChannelLinkReadStore"/>'s own contract already promises
    /// it never returns a row with one) and for any <see cref="ChannelKind"/> this item has not given a
    /// public-web-surface format to (MAX/Telegram/VK/WhatsApp only - Avito never reaches this method at
    /// all, since its own read-store row never exists). A caller sees <see langword="null"/> as "no link
    /// for this row", never a malformed URL.
    /// </summary>
    public static string? BuildUrl(ChannelKind kind, string handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return null;
        }

        return kind switch
        {
            // Telegram's own public bot/channel surface - a bare `@username`, no leading `@` in the URL
            // path itself (confirmed against Telegram's own documented deep-link form, `t.me/<username>`).
            ChannelKind.Telegram => $"https://t.me/{handle}",

            // MAX's own public bot surface mirrors Telegram's shape but keeps the `@` in the path -
            // `max.ru/@<username>`, per this item's own worked example in `docs/backlog/25-148-*.md`.
            ChannelKind.Max => $"https://max.ru/@{handle}",

            // VK's own community deep link, `vk.me/club<id>` - `handle` here is the bare numeric
            // community id (`25-147`'s own read-time-derivation decision; never stored with the "club"
            // prefix baked in, so this is the one and only place that literal joins the id).
            ChannelKind.Vk => $"https://vk.me/club{handle}",

            // WhatsApp's own `wa.me/<digits>` deep link accepts only digits - no `+`, no spaces, no
            // parentheses - while `display_phone_number` (this row's own `handle`, `25-147`'s own
            // capture) is a human-formatted string like "+1 555 0100". Stripped here, at the one point
            // this fact is turned into a URL, rather than normalising it at capture time: `PublicHandle`
            // stays the exact, human-readable value Meta itself returns, which is also the more useful
            // form for a future console screen to display as-is.
            ChannelKind.WhatsApp => $"https://wa.me/{DigitsOnly(handle)}",

            _ => null,
        };
    }

    private static string DigitsOnly(string value) => new([.. value.Where(char.IsAsciiDigit)]);

    /// <summary>
    /// `25-194`: the widget's own channel-switcher display order - the author's own explicit choice,
    /// not alphabetical and not read-store row order (<see cref="Application.Abstractions.IPublicChannelLinkReadStore"/>'s
    /// own SQL carries no <c>ORDER BY</c> at all, so that order is whatever Postgres happens to return -
    /// stable in practice, never a guarantee). Lower sorts first; a kind with no entry here (there is
    /// none among the four this builder ever returns a URL for) sorts last rather than throwing, so a
    /// future fifth channel degrades to "appears at the end" instead of a crash.
    /// </summary>
    public static int DisplayOrder(ChannelKind kind) => kind switch
    {
        ChannelKind.Max => 0,
        ChannelKind.Vk => 1,
        ChannelKind.Telegram => 2,
        ChannelKind.WhatsApp => 3,
        _ => int.MaxValue,
    };
}
