namespace Ago.Chat.Domain;

/// <summary>
/// `23-07`: resolves a site's window counts (loads/opens/conversations) plus its already-resolved
/// <see cref="SiteInstallationState"/> into one <see cref="WidgetFunnelAdvice"/>. Pure, no I/O - the
/// identical "belongs in Domain because nothing here touches a database or a clock" reasoning
/// <see cref="SiteInstallationStateResolver"/> already gives for itself; <c>GetSiteInstallationHandler</c>
/// is the only caller, and every argument here is a value it already read out of Postgres.
///
/// <para><b>Why <paramref name="state"/> joins the three counts rather than the counts alone deciding
/// everything.</b> <see cref="SiteInstallationState.NeverSeenButInUse"/> is `23-06`'s own fourth state -
/// a channel-only tenant whose widget has never once connected - and the item's own Scope is explicit
/// that this state must win outright: "None of the above applies, and the install advice must not be
/// produced." Without this check, a channel-only tenant's window would read `0 / 0 / 0` on every count
/// and this resolver would say <see cref="WidgetFunnelAdvice.FixInstall"/> - exactly the harm
/// `docs/design/decisions.md` §3's own amendment exists to prevent, restated for advice instead of for
/// the raw state.</para>
///
/// <para><b>Order matters, and is checked top to bottom exactly once</b> - the first branch whose
/// count is zero wins, the same "one branch, no combined score" shape decision 2's own amendment (this
/// project's naming rule) takes for a conversation's standard/additional split. A site with zero loads
/// and (necessarily, since a load beacon is what feeds `RecordSightingAsync`) zero opens still gets
/// <see cref="WidgetFunnelAdvice.FixInstall"/>, never <see cref="WidgetFunnelAdvice.ImprovePlacement"/> -
/// fixing the install is the one action that also fixes every stage after it, so it is the only advice
/// that should ever be said out loud for that site.</para>
/// </summary>
public static class WidgetFunnelAdviceResolver
{
    public static WidgetFunnelAdvice Resolve(SiteInstallationState state, int loads, int opens, int conversations)
    {
        if (state == SiteInstallationState.NeverSeenButInUse)
        {
            return WidgetFunnelAdvice.None;
        }

        if (loads == 0)
        {
            return WidgetFunnelAdvice.FixInstall;
        }

        if (opens == 0)
        {
            return WidgetFunnelAdvice.ImprovePlacement;
        }

        if (conversations == 0)
        {
            return WidgetFunnelAdvice.ConnectChannelsAndRespond;
        }

        return WidgetFunnelAdvice.None;
    }

    /// <summary>
    /// `23-07`'s own Scope: "only ever recommend what that tenant can actually act on... advice ending
    /// at 'available on another plan' reads as a sale." The seam this item's Done-when asks for -
    /// "advice naming a capability the tenant's tier does not include is never produced" - kept as its
    /// own method so a future tier-gated capability has exactly one place to plug into, rather than a
    /// branch buried inside <see cref="Resolve"/> itself.
    ///
    /// <para><b>Every advice this resolver can produce is reachable by every tier today, and this
    /// method says so rather than inventing a gate that does not exist.</b> Checked against the whole
    /// codebase while building this: <see cref="Domain.Site.Tier"/> gates seat count
    /// (<c>SubscriptionTierBands</c>) and message retention (<c>RetentionClass.FromTier</c>) and
    /// nothing else - fixing an install, moving the widget's launcher, connecting a channel adapter and
    /// turning on offline auto-reply are all self-service actions open to a free-tier site right now.
    /// `docs/backlog/13-08-*.md`'s own competitor research names WhatsApp/Avito as *Jivo's* paid-tier
    /// channels, not this product's - nothing in `ago-chat` enforces that split today, so pretending it
    /// does here would be `CLAUDE.md`'s own "do not invent numbers" rule applied to a business rule
    /// instead of a benchmark. When a real tier-gated capability exists, this is the one method to
    /// teach it to.</b></para>
    /// </summary>
    public static bool IsReachableForTier(WidgetFunnelAdvice advice, string tier) => true;
}
