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

    /// <summary>`24-05`: a per-site opt-in, off by default - the crux of that item's own Goal. When
    /// <see langword="true"/>, a recorded acceptance (`24-01`) of this site's own visitor-consent
    /// document (<see cref="SiteConsentDocumentKey"/>, published through the tenant's own entry point,
    /// never AGO's) is required before <c>RecordVisitorContactDetailHandler</c> accepts a phone or
    /// email onto a conversation - the gate attaches to <b>handing over contact details</b>, never to
    /// the conversation itself, which stays reachable regardless of this flag
    /// (`RecordVisitorContactDetailHandler`'s own remarks state the gate; nothing here blocks a
    /// message send). Deliberately joins <see cref="NoticeText"/>/<see cref="NoticeUrl"/> on the same
    /// terms - one more fixed, named, validated field on this type, not a new configuration mechanism -
    /// and deliberately defaults to <see langword="false"/> for every existing row
    /// (`Stage24AddSiteRequireContactConsent`'s own column default): `24-05`'s own Scope is explicit
    /// that turning this on for every tenant silently would be worse than leaving it off, so a tenant
    /// who wants it must ask for it.</summary>
    public bool RequireContactConsent { get; }

    /// <summary>`23-63`: whether the launcher draws attention to itself - a brief, bounded, CSS-only
    /// pulse on the closed launcher, off by default. Joins <see cref="RequireContactConsent"/> on
    /// the identical terms: one more fixed, named, validated (here, nothing to validate - a plain
    /// bool has no illegal value) field on this type, not a new configuration mechanism. Deliberately
    /// defaults to <see langword="false"/> for every existing row - a tenant who wants a moving
    /// launcher must ask for it, the same "a tenant cannot consent on their visitor's behalf by
    /// staying silent" posture <c>RequireContactConsent</c> already established for itself, restated
    /// here for a different reason: an unannounced behaviour change (every existing site suddenly
    /// animating on the next deploy) is not something a schema default may cause on its own.</summary>
    public bool AttractAttention { get; }

    /// <summary>`23-64`/`adr/0148`: a bound, matching <see cref="MaxNoticeTextLength"/>'s own
    /// reasoning - this rides the same cached, per-handshake `SiteConfigDto`/wire path on every
    /// visitor bootstrap. Shorter than the notice's 500: a notice is a legal disclosure a tenant may
    /// need room for, a greeting is one drawn line a visitor reads in the seconds before the panel
    /// settles - 300 characters is comfortably longer than that ever needs to be.</summary>
    public const int MaxAutoOpenGreetingTextLength = 300;

    /// <summary>`23-64`: off by default, the identical "a tenant who wants it must ask for it" posture
    /// <see cref="AttractAttention"/> already states for itself - a schema default must not change a
    /// visitor-facing behaviour for every existing site the moment this column exists.</summary>
    public bool AutoOpenEnabled { get; }

    /// <summary>`23-64`: the closed set the backlog item's own Scope fixes - see
    /// <see cref="Domain.AutoOpenDelay"/>'s own remarks. Defaults to
    /// <see cref="Domain.AutoOpenDelay.Seconds30"/>, the author's own stated default, for every site
    /// that has never configured one - including every row that predates this column.</summary>
    public AutoOpenDelay AutoOpenDelaySeconds { get; }

    /// <summary>`23-64`: the tenant's own greeting line, drawn client-side and never sent until the
    /// visitor writes (`adr/0148`) - <see langword="null"/> for every site that has not configured
    /// one, the identical "no default sentence we supply" posture <see cref="NoticeText"/>'s own
    /// remarks already state for the tenant's processing notice ("a greeting in our words on somebody
    /// else's shop is the same mistake `16-04` already forbids for consent text" - the backlog item's
    /// own words). Required, not merely allowed, whenever <see cref="AutoOpenEnabled"/> is
    /// <see langword="true"/> - this constructor throws rather than letting a tenant turn auto-opening
    /// on with nothing to say, which the widget could only have rendered as an empty, silently broken
    /// panel.</summary>
    public string? AutoOpenGreetingText { get; }

    /// <summary>`25-39`: a tenant-level, off-by-default escape hatch around `20-09`'s own verified-phone
    /// gate on the chat-driven booking flow - joins <see cref="RequireContactConsent"/>/
    /// <see cref="AttractAttention"/>/<see cref="AutoOpenEnabled"/> on the identical terms (one more
    /// fixed, named, validated - here, nothing to validate, a plain bool has no illegal value - field on
    /// this type, not a new configuration mechanism). Exists only because `14-15` has no live SMS/voice
    /// gateway account provisioned yet (`docs/backlog/14-15-*`'s own "undecided, needs a cost quote"),
    /// so <c>RequiresVerifiedPhone: true</c> currently makes every chat-driven booking uncompletable
    /// rather than protecting anything - see <c>Ago.Calendar.Application.UseCases.ChatModuleTask.ReplyToModuleTaskHandler</c>'s
    /// own remarks for what turning this on actually changes. Deliberately defaults to
    /// <see langword="false"/> for every existing row, the same "a tenant cannot consent on their
    /// visitor's behalf by staying silent" posture <see cref="RequireContactConsent"/> already
    /// established: a real vendor-verified phone stays the guarantee every tenant gets unless they
    /// explicitly ask to relax it, and the console labels the toggle as a temporary workaround, not a
    /// feature, for the same reason (`WidgetConfigPage.tsx`'s own copy).</summary>
    public bool AcceptUnverifiedPhone { get; }

    /// <summary>`23-78`: the tenant-level default this item's own author decision names -
    /// off by default, joining <see cref="RequireContactConsent"/>/<see cref="AttractAttention"/>/
    /// <see cref="AutoOpenEnabled"/>/<see cref="AcceptUnverifiedPhone"/> on the identical terms (one
    /// more fixed, named, validated - here, nothing to validate, a plain bool has no illegal value -
    /// field on this type, not a new configuration mechanism). Read once, by
    /// <c>StartConversationHandler</c>, to seed a brand-new <see cref="Conversation"/>'s own
    /// <see cref="Conversation.AttachmentUploadGrantedAt"/> at creation time
    /// (<see cref="Conversation.Start"/>'s own <c>attachmentUploadGrantedByDefault</c> parameter) -
    /// changing this flag later never reaches back into a conversation already started, the same
    /// "a stamp taken once, not a live gate" shape <see cref="AcceptUnverifiedPhone"/>'s own consumer
    /// reads it as. An operator can still override the seeded value in either direction for one
    /// specific conversation afterward (`IConversationAttachmentUploadGrantRepository`) - this field
    /// only ever decides what a conversation starts with, never what it stays at.
    ///
    /// Deliberately defaults to <see langword="false"/> for every existing row, the same "a tenant
    /// cannot consent on their visitor's behalf by staying silent" posture <see cref="RequireContactConsent"/>
    /// already established: a shop that fears junk uploads keeps the closed-by-default abuse property
    /// this whole backlog item exists to build; a repair shop or claims desk that wants a photograph in
    /// every second conversation turns this on and pays for it with the corresponding widening of the
    /// vector the item's own "Why this is the strongest abuse control" section names.</summary>
    public bool AllowAttachmentUploadsByDefault { get; }

    public WidgetConfig(
        string? primaryColorHex, Position position, string? noticeText = null, string? noticeUrl = null,
        bool requireContactConsent = false, bool attractAttention = false, bool autoOpenEnabled = false,
        AutoOpenDelay autoOpenDelaySeconds = AutoOpenDelay.Seconds30, string? autoOpenGreetingText = null,
        bool acceptUnverifiedPhone = false, bool allowAttachmentUploadsByDefault = false)
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

        if (autoOpenGreetingText is not null)
        {
            if (string.IsNullOrWhiteSpace(autoOpenGreetingText))
            {
                throw new ArgumentException(
                    "Widget auto-open greeting text cannot be whitespace-only - leave it null for no greeting.",
                    nameof(autoOpenGreetingText));
            }

            if (autoOpenGreetingText.Length > MaxAutoOpenGreetingTextLength)
            {
                throw new ArgumentException(
                    $"Widget auto-open greeting text cannot exceed {MaxAutoOpenGreetingTextLength} characters.",
                    nameof(autoOpenGreetingText));
            }
        }

        // `23-64`: "There is no default sentence we supply" (the backlog item's own Scope) - turning
        // auto-open on with nothing configured to say is not a state this constructor lets exist,
        // the same "an enabled configuration with nothing to say" guard `OfflineAutoReplySettings`
        // already enforces for its own fallback text.
        if (autoOpenEnabled && string.IsNullOrWhiteSpace(autoOpenGreetingText))
        {
            throw new ArgumentException(
                "Widget auto-open cannot be enabled without a greeting - there is no default text.",
                nameof(autoOpenGreetingText));
        }

        PrimaryColorHex = primaryColorHex;
        Position = position;
        NoticeText = noticeText;
        NoticeUrl = noticeUrl;
        RequireContactConsent = requireContactConsent;
        AttractAttention = attractAttention;
        AutoOpenEnabled = autoOpenEnabled;
        AutoOpenDelaySeconds = autoOpenDelaySeconds;
        AutoOpenGreetingText = autoOpenGreetingText;
        AcceptUnverifiedPhone = acceptUnverifiedPhone;
        AllowAttachmentUploadsByDefault = allowAttachmentUploadsByDefault;
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
