namespace Ago.Chat.Domain;

/// <summary>
/// `24-05`: the one place a site's own visitor-consent document key is ever computed - never a
/// caller-supplied string. `24-02`'s <see cref="Document"/>/<see cref="PublishedDocumentVersion"/>
/// mechanism is a flat, global string namespace by design (`Document`'s own remarks: "a document is
/// not tenant-scoped, it is AGO's own"), which is exactly why a tenant's own consent text must never
/// reach it through a caller-chosen key: an operator who could name any key could overwrite AGO's own
/// `privacy-policy` document, or another tenant's, through an endpoint gated only by that operator's
/// own <c>site:configure</c> permission on their own site. Deriving the key here, from
/// <see cref="SiteId"/> and <see cref="VisitorConsentPurpose"/> alone, is what makes that
/// structurally unreachable - <c>PublishDocumentVersionHandler.HandleAsSiteConsentAsync</c> and
/// <c>GetConsentRequirementHandler</c> both call this and never accept a document key from a request
/// body.
///
/// <para>The result satisfies <see cref="Document.Create"/>'s own key-format rule (lowercase letters,
/// digits, single hyphens) because a <see cref="Guid"/>'s default (<c>"D"</c>) format already is
/// exactly that alphabet - lowercase hex and single hyphens, never a leading or trailing one.</para>
/// </summary>
public static class SiteConsentDocumentKey
{
    public static string For(SiteId siteId, VisitorConsentPurpose purpose)
    {
        var segment = purpose switch
        {
            VisitorConsentPurpose.Contact => "contact",
            VisitorConsentPurpose.Marketing => "marketing",
            _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown visitor consent purpose."),
        };

        return $"site-consent-{segment}-{siteId.Value:D}";
    }
}
