using System.Text.RegularExpressions;

namespace Ago.Chat.Domain;

/// <summary>
/// The widget's per-site appearance - `adr/0028`'s two fixed, validated fields (a primary color and a
/// launcher position), deliberately not arbitrary CSS or a free-text theme blob: the widget's Shadow
/// DOM isolation (`embeddable-widget` skill) exists to protect the widget *from* the host page, and a
/// config field that injects style rules the other direction - into a shadow tree the widget's own
/// script controls, sourced from a value a tenant operator supplies - is a real security-surface
/// question this item deliberately does not open (CSS can exfiltrate via attribute selectors, redress
/// content, or abuse <c>:has()</c>/animation timing even confined to a shadow root).
///
/// <see cref="PrimaryColorHex"/> is nullable - <see langword="null"/> means "use the widget's own
/// built-in default," so a freshly self-registered or pre-existing <see cref="Site"/> (<see cref="Default"/>,
/// below) never renders broken just because nobody has configured a color yet.
///
/// `16-04`: <see cref="NoticeText"/>/<see cref="NoticeUrl"/> join on the same terms, not as a new
/// mechanism - fixed, named, validated fields, exactly what `adr/0029` already committed this type to
/// for appearance. Both are nullable and independent (a tenant may set text with no link, a link with
/// no text, or neither - the widget renders nothing when both are empty, per `16-04`'s own Scope: a
/// default notice authored by AGO would be AGO asserting a legal position on the tenant's behalf, which
/// this item must not do). They live here rather than as a sibling method the way <see cref="Site.UpdateLocale"/>
/// added <c>Locale</c>: <c>16-04</c>'s own backlog text scopes them as "two fields in `11-01`'s existing
/// widget configuration", and unlike locale (which the `11-10` write-up found is not "widget
/// appearance" and needs to stay independently identifiable to a future consumer) there is no known
/// future caller that needs color/position and the notice to vary independently - both are the
/// tenant's presentation choices for the same panel, read at the same bootstrap moment.
///
/// A <c>readonly record struct</c>, the same shape <see cref="MessageBody"/> already uses for a
/// validated primitive wrapper - value equality, no identity of its own, validated once at
/// construction so nothing downstream (`Site.UpdateWidgetConfig`, the EF mapping, a mapper building
/// the outbox envelope) has to re-check the hex format - or, now, the URL scheme - it already trusts.
/// </summary>
public readonly partial record struct WidgetConfig
{
    /// <summary>A bound, not a product requirement - the same reasoning <see cref="OfflineAutoReplySettings.MaxRules"/>
    /// states for its own cached, per-handshake-served value: this field rides `SiteConfigDto` into
    /// Redis and onto the wire on every visitor bootstrap, so a tenant cannot turn it into an unbounded
    /// blob. Comfortably longer than a one-line disclaimer needs to be; revisit only if a real tenant's
    /// legitimate notice text hits it.</summary>
    public const int MaxNoticeTextLength = 500;

    public string? PrimaryColorHex { get; }

    public Position Position { get; }

    /// <summary>The tenant's own sentence about who processes what a visitor is about to write -
    /// `16-04`'s Goal. Never authored by AGO; <see langword="null"/> (the default for every existing
    /// row) means the widget shows nothing, not a generic AGO-authored placeholder.</summary>
    public string? NoticeText { get; }

    /// <summary>Where the notice points for detail - the tenant's own policy page, not AGO's. Validated
    /// the same `https://`-only reflex `6-03`'s <c>WebhookUrlValidator</c> applies to a webhook
    /// endpoint (an HMAC payload over plain HTTP defeats its own purpose there; here, a page a browser
    /// is about to navigate to in a new tab has no reason to accept a scheme a modern browser itself
    /// increasingly refuses to treat as safe). Deliberately <b>not</b> reusing
    /// <c>WebhookUrlValidator.Validate</c> itself and deliberately not repeating its private-network/SSRF
    /// check: that check exists because a webhook URL is fetched *by this server*, and a malicious
    /// tenant could point it at an internal address to probe the deployment's own network. This URL is
    /// never fetched server-side - it is only ever handed to a visitor's own browser as an
    /// <c>&lt;a href&gt;</c> the visitor may click, opened in a new context (`ui/widget.ts`,
    /// `ago-widget`) - so the SSRF threat model this reflex exists for does not apply, and copying the
    /// check anyway would reject a tenant's legitimate internal-network policy page (e.g. behind a VPN)
    /// for a risk that was never present.</summary>
    public string? NoticeUrl { get; }

    public WidgetConfig(string? primaryColorHex, Position position, string? noticeText = null, string? noticeUrl = null)
    {
        if (primaryColorHex is not null && !HexColorPattern().IsMatch(primaryColorHex))
        {
            throw new ArgumentException(
                $"'{primaryColorHex}' is not a valid #RRGGBB hex color.", nameof(primaryColorHex));
        }

        if (noticeText is not null)
        {
            if (string.IsNullOrWhiteSpace(noticeText))
            {
                throw new ArgumentException(
                    "Widget notice text cannot be whitespace-only - leave it null to show no notice.",
                    nameof(noticeText));
            }

            if (noticeText.Length > MaxNoticeTextLength)
            {
                throw new ArgumentException(
                    $"Widget notice text cannot exceed {MaxNoticeTextLength} characters.", nameof(noticeText));
            }
        }

        if (noticeUrl is not null && !IsValidHttpsUrl(noticeUrl))
        {
            throw new ArgumentException(
                $"'{noticeUrl}' is not an absolute https:// URL.", nameof(noticeUrl));
        }

        PrimaryColorHex = primaryColorHex;
        Position = position;
        NoticeText = noticeText;
        NoticeUrl = noticeUrl;
    }

    /// <summary>What a <see cref="Site"/> has before anyone ever calls
    /// <see cref="Site.UpdateWidgetConfig"/> - no color override, launcher bottom-right, no notice,
    /// matching `Stage11AddSiteWidgetConfig`'s own column defaults so a row written before this field
    /// existed (or by `1-05`'s seed script, untouched) reads back exactly this value, not a null
    /// reference.
    /// </summary>
    public static readonly WidgetConfig Default = new(null, Position.BottomRight);

    private static bool IsValidHttpsUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColorPattern();
}
