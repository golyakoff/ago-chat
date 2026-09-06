namespace Ago.Chat.Domain;

/// <summary>
/// `24-05`: the two independently-refusable things a visitor's own consent can attach to, named so
/// they are never one control. <see cref="Contact"/> is the one this item's own crux is about -
/// handing over a phone number or an email, gated by <see cref="WidgetConfig.RequireContactConsent"/>
/// when a site turns it on. <see cref="Marketing"/> is "anything beyond the contact itself" (the
/// backlog item's own words) - never gates anything, recorded only if a visitor opts in, and its own
/// document (if the tenant ever publishes one) is a second, separate text from the contact one, so
/// accepting one is never how a visitor accepts the other.
///
/// <para>Stored nowhere as a column of its own - this enum exists only to key
/// <see cref="SiteConsentDocumentKey.For"/> and to select which of the two nullable document summaries
/// <c>GetConsentRequirementHandler</c> resolves. An <see cref="AcceptanceRecord"/> never carries this
/// type directly; it carries the <see cref="SiteConsentDocumentKey"/> string this enum produced, the
/// same "the document is opaque to the acceptance record" shape <see cref="AcceptanceRecord"/>'s own
/// remarks already establish for <see cref="AcceptanceSubjectKind"/>'s sibling, <c>DocumentKey</c>.</para>
/// </summary>
public enum VisitorConsentPurpose
{
    /// <summary>Handing over a phone number or an email - the act `23-09`/`23-10` both end in, and
    /// the one <see cref="WidgetConfig.RequireContactConsent"/> can require an acceptance for before
    /// <c>RecordVisitorContactDetailHandler</c> accepts a row.</summary>
    Contact,

    /// <summary>Anything beyond the contact itself - marketing being the obvious one. Always optional:
    /// no code path in this codebase ever refuses a write because this one is missing.</summary>
    Marketing,
}
